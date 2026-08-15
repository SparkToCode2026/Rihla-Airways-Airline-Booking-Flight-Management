// ============================================================
// Seat map. Load after api.js.
//
// The seat field was a free-text input: you typed "12C", posted it,
// and only then found out from a 409 that it was already sold — or
// from a 400 that row 12 doesn't exist on an 88-seat Embraer.
// Both of those are knowable up front, so this draws the cabin and
// lets people point at a seat instead.
//
// The API has no seat-map endpoint (Airplane stores only Capacity),
// so the layout is derived the same way the server validates it:
// 6 abreast, A-F, rows = ceil(capacity / 6). If that assumption ever
// changes, TicketsController.ValidateSeat is the other place to fix.
// ============================================================

const SEAT_LETTERS = ["A", "B", "C", "D", "E", "F"];
const SEATS_PER_ROW = 6;

// ------------------------------------------------------------
// cabin zones, now driven by the REAL seat classes (whatever's actually
// configured in SeatClasses - Economy/Business/First, or a custom one
// like "Hamza") instead of a hardcoded First/Business/Economy guess. the
// data model still has no per-seat class assignment - SeatClass is a
// property of the TICKET, not of a row - so this is still a visual split,
// but now every class the Cabin dropdown can offer gets an actual zone on
// the map, on every flight. that's the fix for "I picked Business and
// there was nowhere to click": before, a heuristic decided which classes
// even EXISTED on a given aircraft size, and it could disagree with the
// dropdown, which always lists every class the airline has.
//
// pricier classes (higher priceMultiplier) sit at the front and claim a
// smaller share of the rows - a ×4 First cabin gets a quarter the share
// of a ×1 Economy cabin - which is what keeps a 20-row regional jet from
// devoting half its rows to First just because there are only 3 classes
// to split among
// ------------------------------------------------------------
const ZONE_STYLES = [
  { cls: "zone-first", icon: "★" },
  { cls: "zone-business", icon: "◆" },
  { cls: "zone-economy", icon: "✈" },
  { cls: "zone-extra1", icon: "●" },
  { cls: "zone-extra2", icon: "▲" }
];

// a real aircraft this small (a regional jet, roughly) just doesn't carry
// a First cabin - only aircraft with at least this many rows (~120+ seats,
// a proper narrowbody) get the single priciest class. Business (or
// whatever's 2nd) is common enough that it isn't gated the same way
const BIG_AIRCRAFT_ROWS = 20;

// `mustIncludeId` overrides the rarity rule for one specific class - used
// when a ticket ALREADY exists in that class (change-seat modal) so an
// old ticket never becomes un-reseatable just because this particular
// flight's aircraft is "too small" for the class it was sold in
function computeZones(lastRow, classes, mustIncludeId = null) {
  if (!classes || !classes.length) {
    return [{ id: null, name: "Economy", cls: "zone-economy", icon: "✈", from: 1, to: lastRow }];
  }

  const sorted = [...classes].sort((a, b) => b.priceMultiplier - a.priceMultiplier);

  let usable = sorted;
  if (sorted.length > 1 && lastRow < BIG_AIRCRAFT_ROWS) {
    const rarest = sorted[0];
    const keepAnyway = mustIncludeId != null && String(rarest.id) === String(mustIncludeId);
    if (!keepAnyway) usable = sorted.slice(1);
  }

  const weights = usable.map(c => 1 / Math.max(c.priceMultiplier, 0.1));

  // every remaining class gets AT LEAST one row, provided there are
  // enough rows to go around - on a tiny aircraft with more classes than
  // rows, the cheapest classes (the ones actually likely to exist on a
  // small plane) win out
  const maxZones = Math.min(usable.length, lastRow);
  const priced = usable.slice(0, maxZones);
  const prices = weights.slice(0, maxZones);
  const priceTotal = prices.reduce((a, b) => a + b, 0);

  let allocated = priced.map((c, i) => Math.max(1, Math.floor(lastRow * prices[i] / priceTotal)));
  let sum = allocated.reduce((a, b) => a + b, 0);
  // rounding leftovers/shortfalls all land on the LAST zone drawn (the
  // cheapest, biggest cabin) so the rows always add up to exactly lastRow
  allocated[allocated.length - 1] += (lastRow - sum);

  let cursor = 1;
  const zones = [];
  priced.forEach((c, i) => {
    const span = allocated[i];
    const to = i === priced.length - 1 ? lastRow : Math.min(lastRow, cursor + span - 1);
    // style/colour keyed to this class's tier in the FULL sorted list, not
    // its position in `priced` - so Business stays purple whether or not
    // First got excluded above, instead of sliding into First's gold
    const style = ZONE_STYLES[sorted.indexOf(c)] || ZONE_STYLES[ZONE_STYLES.length - 1];
    zones.push({ id: c.id, name: c.name, cls: style.cls, icon: style.icon, from: cursor, to });
    cursor = to + 1;
  });

  return zones;
}

function zoneForRow(zones, row) {
  return zones.find(z => row >= z.from && row <= z.to) || zones[zones.length - 1];
}

// emergency exits. a real single-aisle cabin has one roughly every 5-6
// rows, not just a single pair over the wing - this places one at that
// cadence for the whole cabin, plus one at every class boundary (the
// door business/first hands off through), skipping anything that would
// land right next to another exit
function computeExitRows(lastRow, zones) {
  const exits = new Set();
  if (lastRow <= 6) return exits;

  zones.slice(0, -1).forEach(z => exits.add(z.to));

  const GAP = 6;
  for (let r = GAP; r < lastRow; r += GAP) {
    if (![...exits].some(e => Math.abs(e - r) < 2)) exits.add(r);
  }
  return exits;
}

// NOTE: there can be two maps alive at once - the one in the create form and
// the one inside the "change seat" modal. so nothing here is kept in a shared
// global; each container carries its own target input in a data attribute and
// the click handler reads it back off the DOM.


// GET /flights/{id}/seats gives capacity + every occupied seat number.
// deliberately NOT /tickets/filter - that's scoped to the caller's own
// tickets, so a passenger would see other people's seats as free and only
// discover the clash from a 409 after submitting.
//
// `mine` is the seat this ticket already holds when editing, so it renders
// as available-and-selected rather than as taken by someone else
async function fetchSeatData(flightId, currentSeat = null) {
  const map = await api.get(`/flights/${flightId}/seats`);

  const taken = new Set(map.occupied || []);
  const mine = new Set();
  if (currentSeat) {
    const s = currentSeat.toUpperCase();
    taken.delete(s);
    mine.add(s);
  }
  return { capacity: map.capacity, rows: map.rows, taken, mine };
}


// renders into `containerId` and writes the chosen seat into `inputId`.
// `seatClasses` is the SAME array the Cabin dropdown is built from
// (GET /seatclasses) - pass it through rather than re-fetching here so
// the map and the dropdown are always looking at the same list.
// `activeClassId` is whichever class is currently selected in the Cabin
// dropdown (or the ticket's own class, for the change-seat modal) - every
// seat outside that class's zone is disabled, so it's physically not
// possible to pick a seat that doesn't match the class you're about to
// save the ticket with. it also protects that class from the "rare"
// First-class cutoff below (see computeZones) - returns the zones actually
// drawn so the caller can keep the Cabin dropdown limited to classes this
// particular flight actually has
async function renderSeatMap(containerId, inputId, flightId, currentSeat = null, seatClasses = [], activeClassId = null) {
  const box = document.getElementById(containerId);
  if (!box) return null;

  if (!flightId) {
    box.innerHTML = `<div class="seatmap-empty">Pick a flight to see the seat map.</div>`;
    return null;
  }

  box.innerHTML = `<div class="seatmap-empty">Loading seat map…</div>`;

  let data;
  try {
    data = await fetchSeatData(flightId, currentSeat);
  } catch {
    box.innerHTML = `<div class="seatmap-empty">Could not load seats for this flight.</div>`;
    return null;
  }

  const { capacity, rows: apiRows, taken, mine } = data;
  if (!capacity) {
    box.innerHTML = `<div class="seatmap-empty">This aircraft has no seating configured.</div>`;
    return null;
  }

  const selected = currentSeat ? currentSeat.toUpperCase() : null;
  // the click handler reads this back off the container, so two maps on one
  // page each write to their own field
  box.dataset.seatInput = inputId;
  // remembered so setActiveSeatClass() can re-filter without a re-fetch
  box.dataset.activeClassId = activeClassId ?? "";

  // trust the server's row count - it's the same number ValidateSeat uses
  const lastRow = apiRows || Math.ceil(capacity / SEATS_PER_ROW);
  const freeCount = lastRow * SEATS_PER_ROW - taken.size;

  const zones = computeZones(lastRow, seatClasses, activeClassId);
  // "Business Lavatory" sits after whichever zone is the second-priciest
  // (i.e. Business, when First exists, or the top zone otherwise) - the
  // premium cabin(s) get their own facilities like on a real widebody
  const premiumZone = zones.length > 2 ? zones[zones.length - 3] : (zones.length > 1 ? zones[0] : null);
  const exits = computeExitRows(lastRow, zones);

  // rows are built up PER ZONE, then wrapped as one category card each -
  // "First Class" / "Business Class" / "Economy Class" read as distinct
  // sections you pick a seat within, not just a thin label between rows
  let cabinHtml = "";
  let zoneBuffer = "";
  let currentZone = null;
  let exitNo = 0;

  const flushZone = () => {
    if (!currentZone) return;
    if (zones.length > 1) {
      cabinHtml += `<div class="cabin-zone ${currentZone.cls}" data-zone-id="${currentZone.id ?? ""}">
        <div class="cabin-zone-header ${currentZone.cls}"><span class="zone-icon">${currentZone.icon || ""}</span><span>${escapeHtml(currentZone.name)} Class</span></div>
        ${zoneBuffer}
      </div>`;
    } else {
      // single-class aircraft - no card, no label, just the rows
      cabinHtml += zoneBuffer;
    }
    zoneBuffer = "";
  };

  for (let r = 1; r <= lastRow; r++) {
    const zone = zoneForRow(zones, r);
    if (zone !== currentZone) {
      flushZone();
      currentZone = zone;
    }

    let cells = "";
    SEAT_LETTERS.forEach((letter, i) => {
      // a 3-3 cabin: gangway down the middle, after seat C
      if (i === 3) cells += `<div class="seat-aisle"></div>`;
      const seat = `${r}${letter}`;
      const isTaken = taken.has(seat);
      const isMine = mine.has(seat);
      // wrong-class seats are disabled exactly like taken ones - the
      // Cabin dropdown and the map must never disagree about what's
      // pickable, or a ticket could get saved with a seat that doesn't
      // match the class it's being sold as
      const isWrongClass = activeClassId != null && zone.id != null
        && String(zone.id) !== String(activeClassId) && !isMine;
      const disabled = isTaken || isWrongClass;
      const cls = [
        "seat",
        zone.cls,
        isTaken ? "is-taken" : "",
        isWrongClass ? "is-wrong-class" : "",
        isMine ? "is-mine" : "",
        selected === seat ? "is-picked" : ""
      ].filter(Boolean).join(" ");
      const title = isTaken ? " — already taken"
        : isWrongClass ? ` — ${escapeHtml(zone.name)} only, switch Cabin above to pick here`
        : "";
      cells += `<button type="button" class="${cls}" data-seat="${seat}" data-zone-id="${zone.id ?? ""}"
                  ${disabled ? "disabled" : ""}
                  title="${seat}${title}">${letter}</button>`;
    });
    zoneBuffer += `<div class="seat-row"><span class="seat-row-no">${r}</span>${cells}</div>`;

    if (exits.has(r)) {
      exitNo++;
      zoneBuffer += `<div class="cabin-exit-row"><span class="exit-tag">Emergency Exit ${exitNo}</span><span class="exit-line"></span><span class="exit-tag">Emergency Exit ${exitNo}</span></div>`;
    }

    // a second lavatory serving the premium cabin(s) specifically - sits
    // right where that zone hands off to the next one
    if (premiumZone && r === premiumZone.to) {
      zoneBuffer += `<div class="cabin-lav"><span class="cabin-lav-icon">🚻</span><span>${escapeHtml(premiumZone.name)} Lavatory</span></div>`;
    }
  }
  flushZone();

  box.innerHTML = `
    <div class="seatmap-legend">
      <span><i class="legend-box legend-free"></i> Available (${freeCount})</span>
      <span><i class="legend-box legend-taken"></i> Taken (${taken.size})</span>
      <span><i class="legend-box legend-picked"></i> Your seat</span>
      ${zones.length > 1 ? zones.map(z => `<span><i class="legend-box ${z.cls}"></i> ${escapeHtml(z.name)}</span>`).join("") : ""}
    </div>
    <div class="cabin">
      <div class="cabin-nose">Cockpit</div>
      <div class="cabin-body">${cabinHtml}</div>
      <div class="cabin-tail"><span class="cabin-tail-icon">🚻</span> Aft Galley · Lavatory</div>
    </div>`;

  // one delegated listener on the container rather than one per seat, and
  // only bound once - renderSeatMap runs again every time the flight changes
  if (!box.dataset.bound) {
    box.addEventListener("click", onSeatClick);
    box.dataset.bound = "1";
  }

  return zones;
}


// called whenever the Cabin dropdown changes, WITHOUT re-fetching the
// flight's occupied seats - just re-applies which zone is pickable. if
// the seat that was already picked falls outside the new class's zone,
// it's cleared (a stale seat from the wrong cabin is worse than no seat)
function setActiveSeatClass(containerId, classId) {
  const box = document.getElementById(containerId);
  if (!box) return;
  box.dataset.activeClassId = classId ?? "";

  let clearedSelection = false;
  box.querySelectorAll(".seat").forEach(btn => {
    const zoneId = btn.dataset.zoneId;
    const isTaken = btn.classList.contains("is-taken");
    const isMine = btn.classList.contains("is-mine");
    const wrongClass = zoneId !== "" && classId != null
      && String(zoneId) !== String(classId) && !isMine;

    btn.classList.toggle("is-wrong-class", wrongClass);
    btn.disabled = isTaken || wrongClass;

    if (wrongClass && btn.classList.contains("is-picked")) {
      btn.classList.remove("is-picked");
      clearedSelection = true;
    }
  });

  if (clearedSelection) {
    const input = document.getElementById(box.dataset.seatInput);
    if (input) input.value = "";
  }
}


function onSeatClick(e) {
  const btn = e.target.closest(".seat");
  if (!btn || btn.disabled) return;

  const box = e.currentTarget;
  box.querySelectorAll(".seat.is-picked").forEach(s => s.classList.remove("is-picked"));
  btn.classList.add("is-picked");

  const input = document.getElementById(box.dataset.seatInput);
  if (input) input.value = btn.dataset.seat;
}

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


// renders into `containerId` and writes the chosen seat into `inputId`
async function renderSeatMap(containerId, inputId, flightId, currentSeat = null) {
  const box = document.getElementById(containerId);
  if (!box) return;

  if (!flightId) {
    box.innerHTML = `<div class="seatmap-empty">Pick a flight to see the seat map.</div>`;
    return;
  }

  box.innerHTML = `<div class="seatmap-empty">Loading seat map…</div>`;

  let data;
  try {
    data = await fetchSeatData(flightId, currentSeat);
  } catch {
    box.innerHTML = `<div class="seatmap-empty">Could not load seats for this flight.</div>`;
    return;
  }

  const { capacity, rows: apiRows, taken, mine } = data;
  if (!capacity) {
    box.innerHTML = `<div class="seatmap-empty">This aircraft has no seating configured.</div>`;
    return;
  }

  const selected = currentSeat ? currentSeat.toUpperCase() : null;
  // the click handler reads this back off the container, so two maps on one
  // page each write to their own field
  box.dataset.seatInput = inputId;

  // trust the server's row count - it's the same number ValidateSeat uses
  const lastRow = apiRows || Math.ceil(capacity / SEATS_PER_ROW);
  const freeCount = lastRow * SEATS_PER_ROW - taken.size;

  let rows = "";
  for (let r = 1; r <= lastRow; r++) {
    let cells = "";
    SEAT_LETTERS.forEach((letter, i) => {
      // a 3-3 cabin: gangway down the middle, after seat C
      if (i === 3) cells += `<div class="seat-aisle"></div>`;
      const seat = `${r}${letter}`;
      const isTaken = taken.has(seat);
      const cls = [
        "seat",
        isTaken ? "is-taken" : "",
        mine.has(seat) ? "is-mine" : "",
        selected === seat ? "is-picked" : ""
      ].filter(Boolean).join(" ");
      cells += `<button type="button" class="${cls}" data-seat="${seat}"
                  ${isTaken ? "disabled" : ""}
                  title="${seat}${isTaken ? " — already taken" : ""}">${letter}</button>`;
    });
    rows += `<div class="seat-row"><span class="seat-row-no">${r}</span>${cells}</div>`;
  }

  box.innerHTML = `
    <div class="seatmap-legend">
      <span><i class="legend-box legend-free"></i> Available (${freeCount})</span>
      <span><i class="legend-box legend-taken"></i> Taken (${taken.size})</span>
      <span><i class="legend-box legend-picked"></i> Your seat</span>
    </div>
    ${rows}`;

  // one delegated listener on the container rather than one per seat, and
  // only bound once - renderSeatMap runs again every time the flight changes
  if (!box.dataset.bound) {
    box.addEventListener("click", onSeatClick);
    box.dataset.bound = "1";
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

// Tickets page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("tickets")) return;
  loadPickers().then(loadTickets);
  refreshFlowBar();

  document.getElementById("ticketForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("ticketId").value;
    const body = {
      bookingId: parseInt(document.getElementById("ticketBookingId").value),
      flightId: parseInt(document.getElementById("ticketFlightId").value),
      seatClassId: parseInt(document.getElementById("ticketSeatClassId").value),
      seatNumber: document.getElementById("seatNumber").value,
      passengerName: document.getElementById("passengerName").value
    };
    if (!body.seatNumber) {
      showAlert("Pick a seat from the map before saving.");
      return;
    }

    try {
      if (id) await api.put(`/tickets/${id}`, body);
      else await api.post("/tickets", body);
      uiToast(`Ticket saved · seat ${body.seatNumber}`, "success");
      document.getElementById("ticketForm").reset();
      document.getElementById("ticketId").value = "";
      await loadPickers();
      loadTickets();
      refreshFlowBar();
    } catch (err) { showAlert(err.message); }
  });
});


// when we arrived from "Create Booking", show where the user is in the
// journey and give them the way onward to payment. without this the tickets
// page is a dead end - you add a flight and nothing tells you to go and pay
function flowBookingId() {
  const q = new URLSearchParams(location.search);
  return q.get("flow") === "1" ? Number(q.get("bookingId")) : null;
}

async function refreshFlowBar() {
  const id = flowBookingId();
  if (!id) return;
  try {
    const b = await api.get(`/bookings/${id}`);
    // step 2 until there's something on the booking; once it has a total the
    // next meaningful action is paying for it
    renderFlowBar("flowBar", 2, id, { total: b.totalAmount });
  } catch { /* booking may have been cancelled - just drop the bar */ }
}


// the SAME array the Cabin dropdown is built from - handed to
// renderSeatMap so the map's class zones and the dropdown's options can
// never drift out of sync (see seatmap.js's computeZones)
let seatClassOptions = [];

// current value of the Cabin dropdown, as a number - null while it's
// still empty/unloaded, since "" would otherwise coerce to 0 and match
// a real seat class id
function activeSeatClassId() {
  const v = document.getElementById("ticketSeatClassId")?.value;
  return v ? parseInt(v) : null;
}

// the Cabin dropdown always lists every configured class, but not every
// FLIGHT has every class - a small aircraft doesn't get a First zone (see
// computeZones' rarity rule in seatmap.js). called with whatever
// renderSeatMap just actually drew, this limits the dropdown to classes
// that exist on THIS flight, so picking one always has seats to click -
// same fix in spirit as tying the map to the dropdown in the first place
function syncCabinDropdown(zones) {
  const classSel = document.getElementById("ticketSeatClassId");
  if (!classSel || !zones) return;

  const prevValue = classSel.value;
  const available = seatClassOptions.filter(c => zones.some(z => String(z.id) === String(c.id)));
  const list = available.length ? available : seatClassOptions; // never leave the dropdown empty

  classSel.innerHTML = list
    .map(c => `<option value="${c.id}">${escapeHtml(c.name)} · ×${c.priceMultiplier} fare · ` +
              `${c.baggageAllowanceKg}kg bags</option>`).join("");

  if (list.some(c => String(c.id) === prevValue)) classSel.value = prevValue;

  // whatever the dropdown landed on, the just-rendered map needs to match
  setActiveSeatClass("seatMap", activeSeatClassId());
}

// fills the three pickers from the API. the form used to be raw FK number
// boxes defaulting to 1/1/1 - a passenger can't know their booking's numeric
// id, and flight 1 is a landed flight, so the defaults were guaranteed to fail
async function loadPickers() {
  const me = auth.getUser() || {};

  // only bookings that can still take a ticket: open, and not already paid.
  // /bookings is scoped server-side, so a passenger gets their own and staff
  // get everyone's
  try {
    const bookings = await api.get("/bookings");
    const open = bookings.filter(b => b.status !== "Cancelled" && !b.isPaid);
    const sel = document.getElementById("ticketBookingId");
    sel.innerHTML = open.length
      ? open.map(b => `<option value="${b.id}">#${b.id} · ${escapeHtml(b.customerName)} · ` +
                      `${formatMoney(b.totalAmount)} · ${escapeHtml(b.status)}</option>`).join("")
      : `<option value="">No open unpaid bookings — create one first</option>`;

    // deep link from the bookings page: tickets.html?bookingId=12
    const wanted = new URLSearchParams(location.search).get("bookingId");
    if (wanted && open.some(b => String(b.id) === wanted)) sel.value = wanted;
  } catch (err) { if (err.status !== 403) showAlert(err.message); }

  // seat classes BEFORE flights - the seat map render below needs this
  // list already in hand so its zones match the Cabin dropdown from the
  // very first draw, not just after the user touches it
  try {
    seatClassOptions = await api.get("/seatclasses");
    const classSel = document.getElementById("ticketSeatClassId");
    classSel.innerHTML = seatClassOptions
      .map(c => `<option value="${c.id}">${escapeHtml(c.name)} · ×${c.priceMultiplier} fare · ` +
                `${c.baggageAllowanceKg}kg bags</option>`).join("");

    // switching Cabin re-filters which seats are pickable on the map
    // that's already drawn - it does NOT re-fetch the flight's occupied
    // seats, just disables anything outside the newly chosen class
    if (!classSel.dataset.bound) {
      classSel.addEventListener("change", () => {
        setActiveSeatClass("seatMap", activeSeatClassId());
      });
      classSel.dataset.bound = "1";
    }
  } catch (err) { showAlert(err.message); }

  // future flights only - you can't sell a seat on something that already went
  try {
    const page = await api.get("/flights?pageSize=200");
    const bookable = (page.items || []).filter(f => f.status === "Scheduled" || f.status === "Delayed");
    const flightSel = document.getElementById("ticketFlightId");
    flightSel.innerHTML = bookable
      .map(f => `<option value="${f.id}">${escapeHtml(f.flightNumber)} · ` +
                `${escapeHtml(f.originCode)}→${escapeHtml(f.destinationCode)} · ` +
                `${formatDate(f.departureTime)}</option>`).join("");

    // the seat map belongs to a specific aircraft, so it has to be redrawn
    // whenever the flight changes - a 162-seat 737 and an 88-seat Embraer
    // don't have the same rows, let alone the same seats sold
    if (!flightSel.dataset.bound) {
      flightSel.addEventListener("change", async () => {
        document.getElementById("seatNumber").value = "";
        const zones = await renderSeatMap("seatMap", "seatNumber", flightSel.value, null, seatClassOptions, activeSeatClassId());
        syncCabinDropdown(zones);
      });
      flightSel.dataset.bound = "1";
    }
    const zones = await renderSeatMap("seatMap", "seatNumber", flightSel.value, null, seatClassOptions, activeSeatClassId());
    syncCabinDropdown(zones);
  } catch (err) { showAlert(err.message); }

  // default the traveller to the signed-in user - the common case is booking
  // for yourself, and it was hardcoded to "Nasser Al-Kindi" before
  const nameField = document.getElementById("passengerName");
  if (nameField && !nameField.value) nameField.value = me.name || "";
}

let ticketRows = [];

function renderTicketsTable(data) {
  ticketRows = data;
  document.getElementById("ticketsTable").innerHTML = data.map(t => `
    <tr>
      <td class="mono-link">${t.id}</td>
      <td>Booking #${t.bookingId}</td>
      <td>${escapeHtml(t.flightNumber)} <span class="cell-dim">${escapeHtml(t.originCode)}→${escapeHtml(t.destinationCode)}</span></td>
      <td>${escapeHtml(t.seatNumber)} <span class="cell-dim">${escapeHtml(t.seatClassName)}</span></td>
      <td>${escapeHtml(t.passengerName)}</td>
      <td><b>${formatMoney(t.price)}</b></td>
      <td>
        <div class="row-actions">${ticketActions(t)}</div>
      </td>
    </tr>
  `).join("");
}


// flight statuses PATCH /tickets/{id}/seat itself refuses (TicketsController.
// UpdateSeat) - kept as one list so the button and the API never disagree
// about what's changeable
const SEAT_LOCKED_STATUSES = ["Departed", "Landed", "Cancelled"];

// staff edit and remove tickets; a passenger only picks their seat.
// mirrors the API exactly - PUT and DELETE on /tickets are Admin,Staff, while
// PATCH /tickets/{id}/seat stays self-service. changing the passenger name or
// the seat class re-prices the ticket and is a desk operation
function ticketActions(t) {
  if (isStaffUser()) {
    return `<button class="btn-outline" onclick="editTicket(${t.id})">Edit</button>
            <button class="btn-outline-red" onclick="deleteTicket(${t.id})">Delete</button>`;
  }

  // the flight itself refuses this once it's departed/landed/cancelled -
  // showing a live-looking button that always ends in a 409 reads as a
  // bug, not as a rule. a disabled, textual stand-in with the reason is
  // the honest version of the same button
  if (SEAT_LOCKED_STATUSES.includes(t.flightStatus)) {
    const reason = t.flightStatus === "Cancelled" ? "Flight cancelled" : `Already ${t.flightStatus.toLowerCase()}`;
    return `<button class="btn-outline is-fake" disabled title="${escapeHtml(reason)} — seats can no longer be changed.">${escapeHtml(reason)}</button>`;
  }

  return `<button class="btn-outline" onclick="changeSeat(${t.id})">Change seat</button>`;
}


// the one ticket write a passenger keeps. opens the same seat map the create
// form uses, in a modal, with their current seat pre-selected and every seat
// sold to someone else greyed out
async function changeSeat(id) {
  const t = ticketRows.find(r => r.id === id);
  if (!t) return;

  // changing seat does NOT change class - PATCH /tickets/{id}/seat only
  // ever touches SeatNumber - so the map is locked to whichever class this
  // ticket already holds (t.seatClassId). that's the fix for "if he chose
  // an Economy seat, it should say Economy": the modal shows the ticket's
  // REAL class, not a heuristic guess, and every seat outside that class's
  // zone is disabled - there's nowhere else to click
  const picked = await uiDialog({
    title: `Change seat · ${t.flightNumber}`,
    message: `${t.originCode} → ${t.destinationCode}. ${t.seatClassName} · currently in ${t.seatNumber}.`,
    confirmLabel: "Save seat",
    body: `<input type="hidden" id="seatModalValue" value="${escapeHtml(t.seatNumber)}">
           <div class="seatmap" id="seatModalMap"></div>`,
    // drawn once the modal markup exists, since it has to fetch the flight's
    // occupied seats first
    onOpen: () => renderSeatMap("seatModalMap", "seatModalValue", t.flightId, t.seatNumber, seatClassOptions, t.seatClassId),
    resolveValue: () => document.getElementById("seatModalValue").value,
    // the seat map needs real room to be usable - a 420px centered box
    // was too cramped for a full cabin grid, so this opens near-fullscreen
    fullPage: true
  });

  if (!picked || picked === t.seatNumber) return;

  try {
    await api.patch(`/tickets/${id}/seat`, { seatNumber: picked });
    uiToast(`Seat changed to ${picked.toUpperCase()}`, "success");
    loadTickets();
  } catch (e) { showAlert(e.message); }
}

async function loadTickets() {
  try {
    const data = await api.get("/tickets");
    renderTicketsTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterTickets() {
  const qs = buildQuery({ flightId: "ftFlightId", seatClassId: "ftSeatClassId", minPrice: "ftMinPrice", passengerName: "ftPassenger", destinationCode: "ftDest" });
  try {
    const data = await api.get(`/tickets/filter${qs ? "?" + qs : ""}`);
    renderTicketsTable(data);
  } catch (err) { showAlert(err.message); }
}

function editTicket(id) {
  const t = ticketRows.find(r => r.id === id);
  if (!t) return;
  document.getElementById("ticketId").value = t.id;
  document.getElementById("ticketBookingId").value = t.bookingId;
  document.getElementById("ticketFlightId").value = t.flightId;
  document.getElementById("ticketSeatClassId").value = t.seatClassId;
  document.getElementById("seatNumber").value = t.seatNumber;
  document.getElementById("passengerName").value = t.passengerName;
  window.scrollTo(0, 0);
}
async function deleteTicket(id) {
  if (!await uiConfirm("This removes the ticket and any baggage on it. This cannot be undone.", { title: "Delete ticket", confirmLabel: "Delete", danger: true })) return;
  try { await api.del(`/tickets/${id}`); showAlert("Ticket deleted.", "success"); loadTickets(); } catch (e) { showAlert(e.message); }
}

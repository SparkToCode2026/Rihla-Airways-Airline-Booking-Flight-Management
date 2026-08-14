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
      flightSel.addEventListener("change", () => {
        document.getElementById("seatNumber").value = "";
        renderSeatMap("seatMap", "seatNumber", flightSel.value);
      });
      flightSel.dataset.bound = "1";
    }
    renderSeatMap("seatMap", "seatNumber", flightSel.value);
  } catch (err) { showAlert(err.message); }

  try {
    const classes = await api.get("/seatclasses");
    document.getElementById("ticketSeatClassId").innerHTML = classes
      .map(c => `<option value="${c.id}">${escapeHtml(c.name)} · ×${c.priceMultiplier} fare · ` +
                `${c.baggageAllowanceKg}kg bags</option>`).join("");
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


// staff edit and remove tickets; a passenger only picks their seat.
// mirrors the API exactly - PUT and DELETE on /tickets are Admin,Staff, while
// PATCH /tickets/{id}/seat stays self-service. changing the passenger name or
// the seat class re-prices the ticket and is a desk operation
function ticketActions(t) {
  if (isStaffUser()) {
    return `<button class="btn-outline" onclick="editTicket(${t.id})">Edit</button>
            <button class="btn-outline-red" onclick="deleteTicket(${t.id})">Delete</button>`;
  }
  return `<button class="btn-outline" onclick="changeSeat(${t.id})">Change seat</button>`;
}


// the one ticket write a passenger keeps. opens the same seat map the create
// form uses, in a modal, with their current seat pre-selected and every seat
// sold to someone else greyed out
async function changeSeat(id) {
  const t = ticketRows.find(r => r.id === id);
  if (!t) return;

  const picked = await uiDialog({
    title: `Change seat · ${t.flightNumber}`,
    message: `${t.originCode} → ${t.destinationCode}. Currently in ${t.seatNumber}.`,
    confirmLabel: "Save seat",
    body: `<input type="hidden" id="seatModalValue" value="${escapeHtml(t.seatNumber)}">
           <div class="seatmap" id="seatModalMap"></div>`,
    // drawn once the modal markup exists, since it has to fetch the flight's
    // occupied seats first
    onOpen: () => renderSeatMap("seatModalMap", "seatModalValue", t.flightId, t.seatNumber),
    resolveValue: () => document.getElementById("seatModalValue").value
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

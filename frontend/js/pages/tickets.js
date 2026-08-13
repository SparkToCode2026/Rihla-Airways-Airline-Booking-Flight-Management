// Tickets page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("tickets")) return;
  loadTickets();

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
    try {
      if (id) await api.put(`/tickets/${id}`, body);
      else await api.post("/tickets", body);
      showAlert("Ticket saved successfully", "success");
      document.getElementById("ticketForm").reset();
      document.getElementById("ticketId").value = "";
      loadTickets();
    } catch (err) { showAlert(err.message); }
  });
});

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
        <button class="btn-outline" onclick="editTicket(${t.id})">Edit</button>
        <button class="btn-outline-red" onclick="deleteTicket(${t.id})">Delete</button>
      </td>
    </tr>
  `).join("");
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
  if (!confirm("Delete ticket?")) return;
  try { await api.del(`/tickets/${id}`); showAlert("Ticket deleted.", "success"); loadTickets(); } catch (e) { showAlert(e.message); }
}

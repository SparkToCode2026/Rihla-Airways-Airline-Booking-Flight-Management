// Bookings page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("bookings")) return;
  loadBookings();

  // Create only. BookingCreateDto is just { userId } - date is stamped
  // server-side (UtcNow) and status always starts Pending
  document.getElementById("bookingForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const body = { userId: parseInt(document.getElementById("bookingUserId").value) };
    try {
      await api.post("/bookings", body);
      showAlert("Booking created successfully", "success");
      document.getElementById("bookingForm").reset();
      loadBookings();
    } catch (err) { showAlert(err.message); }
  });
});

function renderBookingsTable(data) {
  document.getElementById("bookingsTable").innerHTML = data.map(b => `
    <tr>
      <td class="mono-link">${b.id}</td>
      <td>${escapeHtml(b.customerName)} <span class="cell-dim">#${b.userId}</span></td>
      <td>${formatDate(b.bookingDate)}</td>
      <td><b>${formatMoney(b.totalAmount)}</b></td>
      <td>${statusBadge(b.status)}</td>
      <td>
        <div class="row-actions">
          ${b.status === "Pending" ? `<button class="btn-outline" onclick="patchStatus('/bookings/${b.id}/status',{status:'Confirmed'},[loadBookings])">Confirm</button>` : ""}
          ${b.status !== "Cancelled" ? `<button class="btn-outline-red" onclick="patchStatus('/bookings/${b.id}/status',{status:'Cancelled'},[loadBookings],'Cancel this booking?')">Cancel</button>` : ""}
          <button class="btn-outline" onclick="recalcBooking(${b.id})" title="Re-sum the total from this booking's tickets">Recalculate</button>
          <button class="btn-outline-red" onclick="deleteBooking(${b.id})">Delete</button>
        </div>
      </td>
    </tr>
  `).join("");
}

async function loadBookings() {
  try {
    const data = await api.get("/bookings");
    renderBookingsTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterBookings() {
  const qs = buildQuery({ status: "fbStatus", fromDate: "fbFrom", toDate: "fbTo", minTotal: "fbMinTotal", customerEmail: "fbEmail", unpaidOnly: "fbUnpaid" });
  try {
    const data = await api.get(`/bookings/filter${qs ? "?" + qs : ""}`);
    renderBookingsTable(data);
  } catch (err) { showAlert(err.message); }
}

// BookingUpdateDto carries no fields - PUT recalculates TotalAmount from the
// booking's tickets server-side. this is the repair tool for a booking that
// drifted, not a form edit
async function recalcBooking(id) {
  try { await api.put(`/bookings/${id}`, {}); showAlert("Booking total recalculated", "success"); loadBookings(); }
  catch (err) { showAlert(err.message); }
}

async function deleteBooking(id) {
  if (!confirm("Delete booking?")) return;
  try { await api.del(`/bookings/${id}`); showAlert("Booking deleted.", "success"); loadBookings(); } catch (e) { showAlert(e.message); }
}

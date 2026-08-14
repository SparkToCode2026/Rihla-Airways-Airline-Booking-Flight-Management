// Bookings page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("bookings")) return;
  loadBookings();

  // the User ID box used to be a number input hardcoded to value="1".
  // user 1 is the Admin, so EVERY passenger who pressed "Create Booking" sent
  // userId:1, failed the ownership check and got a 403 - the booking flow was
  // impossible for the exact role it exists for.
  // a passenger can only book for themselves, so the field is filled in and
  // hidden for them. staff booking on a customer's behalf still need it
  const me = auth.getUser() || {};
  const userField = document.getElementById("bookingUserId");
  const userWrap = document.getElementById("bookingUserGroup");

  userField.value = me.userId;
  if (!isStaffUser() && userWrap) userWrap.style.display = "none";

  // Create only. BookingCreateDto is just { userId } - date is stamped
  // server-side (UtcNow) and status always starts Pending
  document.getElementById("bookingForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    // fall back to the caller's own id rather than NaN when the field is hidden
    const body = { userId: parseInt(userField.value, 10) || me.userId };
    try {
      const created = await api.post("/bookings", body);
      // a fresh booking is an empty shell - 0.00, no flights, nothing to pay.
      // leaving the user on this list is where the old flow lost people, so
      // take them straight to the next step instead
      if (!isStaffUser()) {
        location.href = `tickets.html?bookingId=${created.id}&flow=1`;
        return;
      }
      uiToast(`Booking #${created.id} created`, "success");
      userField.value = me.userId;
      loadBookings();
    } catch (err) { showAlert(err.message); }
  });

  // arriving back from the tickets page with ?pay=<id>: jump to that row and
  // open the payment straight away, so "Continue to payment" actually pays
  const payFor = Number(new URLSearchParams(location.search).get("pay"));
  if (payFor) {
    document.addEventListener("bookings:loaded", function once() {
      document.removeEventListener("bookings:loaded", once);
      const row = document.querySelector(`tr[data-booking="${payFor}"]`);
      row?.classList.add("is-highlighted");
      row?.scrollIntoView({ behavior: "smooth", block: "center" });
      const b = bookingRows.find(x => x.id === payFor);
      if (b && !b.isPaid && b.totalAmount > 0) payBooking(payFor);
    });
  }
});

let bookingRows = [];

function renderBookingsTable(data) {
  bookingRows = data;
  document.getElementById("bookingsTable").innerHTML = data.map(b => `
    <tr data-booking="${b.id}">
      <td class="mono-link">${b.id}</td>
      <td>${escapeHtml(b.customerName)} <span class="cell-dim">#${b.userId}</span></td>
      <td>${formatDate(b.bookingDate)}</td>
      <td><b>${formatMoney(b.totalAmount)}</b></td>
      <td>${statusBadge(b.status)}</td>
      <td><span class="cell-dim">${b.isPaid ? escapeHtml(b.paymentStatus) : "unpaid"}</span></td>
      <td>
        <div class="row-actions">${bookingActions(b)}</div>
      </td>
    </tr>
  `).join("");
}


// only render what this role can actually do. before, a passenger saw
// Confirm / Cancel / Recalculate / Delete and three of the four were a
// guaranteed error - Confirm 409s on an unpaid booking, Recalculate is a
// staff PUT and Delete is Admin-only
function bookingActions(b) {
  const acts = [];
  const open = b.status !== "Cancelled";

  if (isStaffUser()) {
    if (b.status === "Pending")
      acts.push(`<button class="btn-outline" onclick="patchStatus('/bookings/${b.id}/status',{status:'Confirmed'},[loadBookings])">Confirm</button>`);
    if (open)
      acts.push(`<button class="btn-outline-red" onclick="patchStatus('/bookings/${b.id}/status',{status:'Cancelled'},[loadBookings],'Cancel this booking?')">Cancel</button>`);
    acts.push(`<button class="btn-outline" onclick="recalcBooking(${b.id})" title="Re-sum the total from this booking's tickets">Recalculate</button>`);
    if (isAdminUser())
      acts.push(`<button class="btn-outline-red" onclick="deleteBooking(${b.id})">Delete</button>`);
    return acts.join("");
  }

  // --- passenger: the actual self-service journey ---
  if (open && !b.isPaid)
    acts.push(`<a class="btn-outline" href="tickets.html?bookingId=${b.id}">Add flight</a>`);

  // pay is only meaningful once there's something to pay for
  if (open && !b.isPaid && b.totalAmount > 0)
    acts.push(
      `<select id="payMethod-${b.id}" class="select-inline">
         <option value="Card">Card</option>
         <option value="Cash">Cash</option>
         <option value="BankTransfer">Bank transfer</option>
       </select>
       <button class="btn-outline" onclick="payBooking(${b.id})">Pay ${formatMoney(b.totalAmount)}</button>`);

  // a paid booking has to be refunded before it can be cancelled, which is a
  // desk job - so only offer Cancel while there's no money on it
  if (open && !b.isPaid)
    acts.push(`<button class="btn-outline-red" onclick="patchStatus('/bookings/${b.id}/status',{status:'Cancelled'},[loadBookings],'Cancel this booking?')">Cancel</button>`);

  return acts.join("") || `<span class="cell-dim">—</span>`;
}


// the step that was missing entirely: a passenger had no way to pay for their
// own booking, because the Payments page is Admin/Staff only. the amount is
// never typed by hand - it has to equal the booking total exactly or the API
// rejects it, so we send the total straight back
async function payBooking(id) {
  const b = bookingRows.find(x => x.id === id);
  if (!b) return;

  const sel = document.getElementById(`payMethod-${id}`);
  const method = sel ? sel.value : "Card";

  if (!await uiConfirm(
        `${formatMoney(b.totalAmount)} for booking #${id}, paid by ${method}.` +
        (method === "Card"
          ? " Card payments confirm the booking straight away."
          : " Cash and bank transfers are confirmed by staff at the desk."),
        { title: "Confirm payment", confirmLabel: `Pay ${formatMoney(b.totalAmount)}` })) return;

  try {
    const p = await api.post("/payments", {
      bookingId: id,
      amount: b.totalAmount,
      method
    });
    uiToast(
      p.status === "Completed"
        ? `Payment received — booking #${id} confirmed. E-ticket on its way.`
        : `Payment recorded as ${p.status}. Staff will confirm it at the desk.`,
      "success", 5000);
    // drop ?pay= so a refresh doesn't reopen the payment dialog
    history.replaceState({}, "", location.pathname);
    loadBookings();
  } catch (err) { showAlert(err.message); }
}

async function loadBookings() {
  try {
    const data = await api.get("/bookings");
    renderBookingsTable(data);
    document.dispatchEvent(new Event("bookings:loaded"));
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
  if (!await uiConfirm("This removes the booking and everything attached to it. This cannot be undone.", { title: "Delete booking", confirmLabel: "Delete", danger: true })) return;
  try { await api.del(`/bookings/${id}`); showAlert("Booking deleted.", "success"); loadBookings(); } catch (e) { showAlert(e.message); }
}

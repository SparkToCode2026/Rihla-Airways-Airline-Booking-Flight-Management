// Payments page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("payments")) return;
  loadPayments();

  // create takes { bookingId, amount, method }, status always starts
  // Pending. edit (Pending payments only) takes { amount, method }
  document.getElementById("paymentForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("paymentId")?.value;
    const amount = parseFloat(document.getElementById("paymentAmount").value);
    const method = document.getElementById("paymentMethod").value;
    try {
      if (id) await api.put(`/payments/${id}`, { amount, method });
      else await api.post("/payments", { bookingId: parseInt(document.getElementById("paymentBookingId").value), amount, method });
      showAlert("Payment saved successfully", "success");
      document.getElementById("paymentForm").reset();
      if (document.getElementById("paymentId")) document.getElementById("paymentId").value = "";
      loadPayments();
    } catch (err) { showAlert(err.message); }
  });
});

let paymentRows = [];

function renderPaymentsTable(data) {
  paymentRows = data;
  document.getElementById("paymentsTable").innerHTML = data.map(p => `
    <tr>
      <td class="mono-link">${p.id}</td>
      <td>${escapeHtml(p.customerName)} <span class="cell-dim">Booking #${p.bookingId}</span></td>
      <td><b>${formatMoney(p.amount)}</b></td>
      <td>${formatDate(p.paymentDate)}</td>
      <td>${escapeHtml(p.method)}</td>
      <td>${statusBadge(p.status)}</td>
      <td>
        <div class="row-actions">
          ${p.status === "Pending" ? `<button class="btn-outline" onclick="patchStatus('/payments/${p.id}/status',{status:'Completed'},[loadPayments])">Complete</button>` : ""}
          ${p.status === "Pending" ? `<button class="btn-outline-red" onclick="patchStatus('/payments/${p.id}/status',{status:'Failed'},[loadPayments])">Fail</button>` : ""}
          ${p.status === "Failed" ? `<button class="btn-outline" onclick="patchStatus('/payments/${p.id}/status',{status:'Pending'},[loadPayments])">Retry</button>` : ""}
          ${p.status === "Completed" ? `<button class="btn-outline-red" onclick="patchStatus('/payments/${p.id}/status',{status:'Refunded'},[loadPayments],'Refund this payment?')">Refund</button>` : ""}
          ${p.status === "Pending" ? `<button class="btn-outline" onclick="editPayment(${p.id})">Edit</button>` : ""}
          <button class="btn-outline-red" onclick="deletePayment(${p.id})">Delete</button>
        </div>
      </td>
    </tr>
  `).join("");
}

async function loadPayments() {
  try {
    const data = await api.get("/payments");
    renderPaymentsTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterPayments() {
  const qs = buildQuery({ status: "fpayStatus", method: "fpayMethod", minAmount: "fpayMinAmount", fromDate: "fpayFrom", toDate: "fpayTo" });
  try {
    const data = await api.get(`/payments/filter${qs ? "?" + qs : ""}`);
    renderPaymentsTable(data);
  } catch (err) { showAlert(err.message); }
}

function editPayment(id) {
  const p = paymentRows.find(r => r.id === id);
  if (!p) return;
  // PaymentUpdateDto only accepts amount + method, and only while Pending -
  // the backend rejects an edit on a Completed/Refunded payment anyway
  if (p.status !== "Pending") { showAlert("Only a Pending payment can be edited.", "warning"); return; }
  let idField = document.getElementById("paymentId");
  if (!idField) {
    idField = document.createElement("input");
    idField.type = "hidden";
    idField.id = "paymentId";
    document.getElementById("paymentForm").prepend(idField);
  }
  idField.value = p.id;
  document.getElementById("paymentBookingId").value = p.bookingId;
  document.getElementById("paymentAmount").value = p.amount;
  document.getElementById("paymentMethod").value = p.method;
  window.scrollTo(0, 0);
}
async function deletePayment(id) {
  if (!confirm("Delete payment?")) return;
  try { await api.del(`/payments/${id}`); showAlert("Payment deleted.", "success"); loadPayments(); } catch (e) { showAlert(e.message); }
}

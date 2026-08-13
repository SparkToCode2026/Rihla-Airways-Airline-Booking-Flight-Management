// Baggage page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("baggage")) return;
  loadBaggage();

  // BaggageNumber is assigned server-side (next in sequence per ticket) -
  // not something the client picks
  document.getElementById("baggageForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("baggageId").value;
    const body = id ? {
      weightKg: parseFloat(document.getElementById("weightKg").value),
      type: document.getElementById("baggageType").value
    } : {
      ticketId: parseInt(document.getElementById("baggageTicketId").value),
      weightKg: parseFloat(document.getElementById("weightKg").value),
      type: document.getElementById("baggageType").value
    };
    try {
      if (id) await api.put(`/baggages/${id}`, body);
      else await api.post("/baggages", body);
      showAlert("Baggage item saved successfully", "success");
      document.getElementById("baggageForm").reset();
      document.getElementById("baggageId").value = "";
      document.getElementById("baggageTicketId").disabled = false;
      loadBaggage();
    } catch (err) { showAlert(err.message); }
  });
});

let baggageRows = [];

function renderBaggageTable(data) {
  baggageRows = data;
  document.getElementById("baggageTable").innerHTML = data.map(b => `
    <tr>
      <td class="mono-link">${b.id}</td>
      <td>${escapeHtml(b.flightNumber)} <span class="cell-dim">${escapeHtml(b.passengerName)}, seat ${escapeHtml(b.seatNumber)} <span class="cell-dim">(Ticket #${b.ticketId})</span></span></td>
      <td>#${b.baggageNumber}</td>
      <td>${b.weightKg} kg</td>
      <td>${escapeHtml(b.type)}</td>
      <td><b>${formatMoney(b.fee)}</b></td>
      <td>
        <button class="btn-outline" onclick="editBaggage(${b.id})">Edit</button>
        <button class="btn-outline-red" onclick="deleteBaggage(${b.id})">Delete</button>
      </td>
    </tr>
  `).join("");
}

async function loadBaggage() {
  try {
    // controller class is BaggagesController -> "api/Baggages", plural
    const data = await api.get("/baggages");
    renderBaggageTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterBaggage() {
  const qs = buildQuery({ type: "fbgType", minWeight: "fbgMinWeight", maxWeight: "fbgMaxWeight", flightId: "fbgFlightId", overweightOnly: "fbgOverweight" });
  try {
    const data = await api.get(`/baggages/filter${qs ? "?" + qs : ""}`);
    renderBaggageTable(data);
  } catch (err) { showAlert(err.message); }
}

function editBaggage(id) {
  const b = baggageRows.find(r => r.id === id);
  if (!b) return;
  document.getElementById("baggageId").value = b.id;
  document.getElementById("baggageTicketId").value = b.ticketId;
  document.getElementById("baggageTicketId").disabled = true; // not updatable
  document.getElementById("weightKg").value = b.weightKg;
  document.getElementById("baggageType").value = b.type;
  window.scrollTo(0, 0);
}
async function deleteBaggage(id) {
  if (!confirm("Delete baggage?")) return;
  try { await api.del(`/baggages/${id}`); showAlert("Baggage deleted.", "success"); loadBaggage(); } catch (e) { showAlert(e.message); }
}

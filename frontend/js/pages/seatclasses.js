// only roles that can write this resource get row actions - the API
// enforces it too, this just stops the UI offering a guaranteed 403
const canWrite = () => isAdminUser();

// Seat Classes page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("seatclasses")) return;
  loadSeatClasses();

  document.getElementById("seatClassForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("seatClassId").value;
    const body = {
      name: document.getElementById("className").value,
      priceMultiplier: parseFloat(document.getElementById("priceMultiplier").value),
      baggageAllowanceKg: parseInt(document.getElementById("baggageAllowanceKg").value)
    };
    try {
      if (id) await api.put(`/seatclasses/${id}`, body);
      else await api.post("/seatclasses", body);
      showAlert("Seat Class saved successfully", "success");
      document.getElementById("seatClassForm").reset();
      document.getElementById("seatClassId").value = "";
      loadSeatClasses();
    } catch (err) { showAlert(err.message); }
  });
});

let seatClassRows = [];

function renderSeatClassesTable(data) {
  seatClassRows = data;
  document.getElementById("seatclassesTable").innerHTML = data.map(sc => `
    <tr>
      <td class="mono-link">${sc.id}</td>
      <td><b>${escapeHtml(sc.name)}</b></td>
      <td>x${sc.priceMultiplier}</td>
      <td>${sc.baggageAllowanceKg} kg</td>
      ${canWrite() ? `
      <td>
        <button class="btn-outline" onclick="editSeatClass(${sc.id})">Edit</button><button class="btn-outline-red" onclick="deleteSeatClass(${sc.id})">Delete</button>
      </td>` : ``}
    </tr>
  `).join("");
}

async function loadSeatClasses() {
  try {
    // SeatClassesController -> "api/SeatClasses", no hyphen
    const data = await api.get("/seatclasses");
    renderSeatClassesTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterSeatClasses() {
  const qs = buildQuery({ name: "fscName", minMultiplier: "fscMinMult", maxMultiplier: "fscMaxMult", minBaggageKg: "fscMinBag" });
  try {
    const data = await api.get(`/seatclasses/filter${qs ? "?" + qs : ""}`);
    renderSeatClassesTable(data);
  } catch (err) { showAlert(err.message); }
}

function editSeatClass(id) {
  const sc = seatClassRows.find(r => r.id === id);
  if (!sc) return;
  document.getElementById("seatClassId").value = sc.id;
  document.getElementById("className").value = sc.name;
  document.getElementById("priceMultiplier").value = sc.priceMultiplier;
  document.getElementById("baggageAllowanceKg").value = sc.baggageAllowanceKg;
  window.scrollTo(0, 0);
}
async function deleteSeatClass(id) {
  if (!await uiConfirm("Seat classes are referenced by existing tickets, so this may be refused.", { title: "Delete seat class", confirmLabel: "Delete", danger: true })) return;
  try { await api.del(`/seatclasses/${id}`); showAlert("Seat class deleted.", "success"); loadSeatClasses(); } catch (e) { showAlert(e.message); }
}

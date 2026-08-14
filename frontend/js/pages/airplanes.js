// Airplanes page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("airplanes")) return;
  loadAirplanes();

  document.getElementById("airplaneForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("airplaneId").value;
    const body = {
      model: document.getElementById("model").value,
      registrationNumber: document.getElementById("registrationNumber").value,
      capacity: parseInt(document.getElementById("capacity").value),
      manufactureYear: parseInt(document.getElementById("manufactureYear").value)
    };
    try {
      if (id) await api.put(`/airplanes/${id}`, body);
      else await api.post("/airplanes", body);
      showAlert("Airplane saved successfully", "success");
      document.getElementById("airplaneForm").reset();
      document.getElementById("airplaneId").value = "";
      loadAirplanes();
    } catch (err) { showAlert(err.message); }
  });
});

let airplaneRows = [];

function renderAirplanesTable(data) {
  airplaneRows = data;
  document.getElementById("airplanesTable").innerHTML = data.map(a => `
    <tr>
      <td class="mono-link">${a.id}</td>
      <td><b>${escapeHtml(a.model)}</b></td>
      <td>${escapeHtml(a.registrationNumber)}</td>
      <td>${a.capacity}</td>
      <td>${a.manufactureYear}</td>
      <td>
        <button class="btn-outline" onclick="editAirplane(${a.id})">Edit</button>
        <button class="btn-outline-red" onclick="deleteAirplane(${a.id})">Delete</button>
      </td>
    </tr>
  `).join("");
}

async function loadAirplanes() {
  try {
    const data = await api.get("/airplanes");
    renderAirplanesTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterAirplanes() {
  const qs = buildQuery({ model: "fplModel", minYear: "fplMinYear", maxYear: "fplMaxYear", minCapacity: "fplMinCap", freeOn: "fplFreeOn", idleOnly: "fplIdle" });
  try {
    const data = await api.get(`/airplanes/filter${qs ? "?" + qs : ""}`);
    renderAirplanesTable(data);
  } catch (err) { showAlert(err.message); }
}

function editAirplane(id) {
  const a = airplaneRows.find(r => r.id === id);
  if (!a) return;
  document.getElementById("airplaneId").value = a.id;
  document.getElementById("model").value = a.model;
  document.getElementById("registrationNumber").value = a.registrationNumber;
  document.getElementById("capacity").value = a.capacity;
  document.getElementById("manufactureYear").value = a.manufactureYear;
  window.scrollTo(0, 0);
}
async function deleteAirplane(id) {
  if (!await uiConfirm("Aircraft assigned to a flight cannot be removed.", { title: "Delete airplane", confirmLabel: "Delete", danger: true })) return;
  try { await api.del(`/airplanes/${id}`); showAlert("Airplane deleted.", "success"); loadAirplanes(); } catch (e) { showAlert(e.message); }
}

// Routes page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("routes")) return;
  loadRoutes();

  document.getElementById("routeForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("routeId").value;
    const body = {
      originAirportId: parseInt(document.getElementById("originAirportId").value),
      destinationAirportId: parseInt(document.getElementById("destinationAirportId").value),
      distanceKm: parseInt(document.getElementById("distanceKm").value),
      estimatedDurationMin: parseInt(document.getElementById("estimatedDurationMin").value)
    };
    try {
      if (id) await api.put(`/routes/${id}`, body);
      else await api.post("/routes", body);
      showAlert("Route saved successfully", "success");
      document.getElementById("routeForm").reset();
      document.getElementById("routeId").value = "";
      loadRoutes();
    } catch (err) { showAlert(err.message); }
  });
});

let routeRows = [];

function renderRoutesTable(data) {
  routeRows = data;
  document.getElementById("routesTable").innerHTML = data.map(r => `
    <tr>
      <td class="mono-link">${r.id}</td>
      <td>${escapeHtml(r.originCode)} <span class="cell-dim">#${r.originAirportId}</span></td>
      <td>${escapeHtml(r.destinationCode)} <span class="cell-dim">#${r.destinationAirportId}</span></td>
      <td>${r.distanceKm} km</td>
      <td>${r.estimatedDurationMin} mins</td>
      <td>
        <button class="btn-outline" onclick="editRoute(${r.id})">Edit</button>
        <button class="btn-outline-red" onclick="deleteRoute(${r.id})">Delete</button>
      </td>
    </tr>
  `).join("");
}

async function loadRoutes() {
  try {
    const data = await api.get("/routes");
    renderRoutesTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterRoutes() {
  const qs = buildQuery({ originId: "frOriginId", destinationId: "frDestId", maxDistance: "frMaxDist", originCode: "frOriginCode", destinationCountry: "frDestCountry" });
  try {
    const data = await api.get(`/routes/filter${qs ? "?" + qs : ""}`);
    renderRoutesTable(data);
  } catch (err) { showAlert(err.message); }
}

function editRoute(id) {
  const r = routeRows.find(x => x.id === id);
  if (!r) return;
  document.getElementById("routeId").value = r.id;
  document.getElementById("originAirportId").value = r.originAirportId;
  document.getElementById("destinationAirportId").value = r.destinationAirportId;
  document.getElementById("distanceKm").value = r.distanceKm;
  document.getElementById("estimatedDurationMin").value = r.estimatedDurationMin;
  window.scrollTo(0, 0);
}
async function deleteRoute(id) {
  if (!confirm("Delete route?")) return;
  try { await api.del(`/routes/${id}`); showAlert("Route deleted.", "success"); loadRoutes(); } catch (e) { showAlert(e.message); }
}

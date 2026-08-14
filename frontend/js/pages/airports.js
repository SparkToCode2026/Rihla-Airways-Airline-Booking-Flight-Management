let airportPager;

// only roles that can write this resource get row actions - the API
// enforces it too, this just stops the UI offering a guaranteed 403
const canWrite = () => isAdminUser();

// Airports page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("airports")) return;

  // the pager holds the full result set and hands renderAirportsGrid one page
  // at a time; every load/filter goes through setData so paging survives a filter
  airportPager = createPager({
    pageSize: 12,          // 12 cards fills the 3-across grid evenly
    mountId: "airportsPager",
    onRender: renderAirportsGrid
  });
  loadAirports();

  document.getElementById("airportForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("airportId").value;
    const body = {
      code: document.getElementById("airportCode").value,
      name: document.getElementById("airportName").value,
      city: document.getElementById("airportCity").value,
      country: document.getElementById("airportCountry").value
    };
    try {
      if (id) await api.put(`/airports/${id}`, body);
      else await api.post("/airports", body);
      showAlert("Airport saved successfully", "success");
      document.getElementById("airportForm").reset();
      document.getElementById("airportId").value = "";
      loadAirports();
    } catch (err) { showAlert(err.message); }
  });
});

let airportRows = [];

function renderAirportsGrid(data) {
  airportRows = data;
  document.getElementById("airportsGrid").innerHTML = data.map(a => `
    <div class="airport-card">
      <div class="airport-top">
        <div class="airport-code">${escapeHtml(a.code)}</div>
        <div class="tag">${escapeHtml(a.country)}</div>
      </div>
      <div class="airport-name">${escapeHtml(a.name)}</div>
      <div class="airport-city">${escapeHtml(a.city)}</div>
      <div class="airport-stats">
        <div>
          <div class="airport-stat-label">System ID</div>
          <div class="airport-stat-value">#${a.id}</div>
        </div>
      </div>
      <div class="airport-actions">
        ${canWrite() ? `<button class="btn-outline" onclick="editAirport(${a.id})">Edit</button><button class="btn-outline-red" onclick="deleteAirport(${a.id})">Delete</button>` : `<span class="cell-dim">—</span>`}
      </div>
    </div>
  `).join("");
}

async function loadAirports() {
  try {
    const data = await api.get("/airports");
    airportPager.setData(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterAirports() {
  const qs = buildQuery({ country: "fapCountry", city: "fapCity", code: "fapCode", search: "fapSearch", connectedOnly: "fapConnected" });
  try {
    const data = await api.get(`/airports/filter${qs ? "?" + qs : ""}`);
    airportPager.setData(data);
  } catch (err) { showAlert(err.message); }
}

function editAirport(id) {
  const a = airportRows.find(r => r.id === id);
  if (!a) return;
  document.getElementById("airportId").value = a.id;
  document.getElementById("airportCode").value = a.code;
  document.getElementById("airportName").value = a.name;
  document.getElementById("airportCity").value = a.city;
  document.getElementById("airportCountry").value = a.country;
  window.scrollTo(0, 0);
}
async function deleteAirport(id) {
  if (!await uiConfirm("Airports used by a route cannot be removed.", { title: "Delete airport", confirmLabel: "Delete", danger: true })) return;
  try { await api.del(`/airports/${id}`); showAlert("Airport deleted.", "success"); loadAirports(); } catch (e) { showAlert(e.message); }
}

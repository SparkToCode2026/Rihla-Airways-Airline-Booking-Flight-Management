// only roles that can write this resource get row actions - the API
// enforces it too, this just stops the UI offering a guaranteed 403
const canWrite = () => isStaffUser();

// Flights page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("flights")) return;
  loadFlights();

  document.getElementById("flightForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("flightId").value;
    // RouteId is only accepted on create - FlightUpdateDto doesn't have it,
    // changing where an existing flight goes is a different operation
    const body = id ? {
      flightNumber: document.getElementById("flightNumber").value,
      airplaneId: parseInt(document.getElementById("flightAirplaneId").value),
      departureTime: document.getElementById("departureTime").value,
      arrivalTime: document.getElementById("arrivalTime").value
    } : {
      flightNumber: document.getElementById("flightNumber").value,
      routeId: parseInt(document.getElementById("flightRouteId").value),
      airplaneId: parseInt(document.getElementById("flightAirplaneId").value),
      departureTime: document.getElementById("departureTime").value,
      arrivalTime: document.getElementById("arrivalTime").value
    };
    try {
      if (id) await api.put(`/flights/${id}`, body);
      else await api.post("/flights", body);
      showAlert("Flight saved successfully", "success");
      document.getElementById("flightForm").reset();
      document.getElementById("flightId").value = "";
      document.getElementById("flightRouteId").disabled = false;
      loadFlights();
    } catch (err) { showAlert(err.message); }
  });
});

const FLIGHT_STATUSES = ["Scheduled", "Boarding", "Departed", "Delayed", "Landed", "Cancelled"];

// last-fetched rows, kept module-level so editFlight(id) can look a row up
// instead of the button embedding JSON.stringify(f) inside a single-quoted
// onclick attribute - that broke the moment a value contained an apostrophe
let flightRows = [];

function renderFlightsTable(data) {
  flightRows = data;
  document.getElementById("flightsTable").innerHTML = data.map(f => `
    <tr>
      <td class="mono-link">${f.id}</td>
      <td><b>${escapeHtml(f.flightNumber)}</b></td>
      <td>${escapeHtml(f.originCode)} → ${escapeHtml(f.destinationCode)} <span class="cell-dim">(Route #${f.routeId})</span></td>
      <td>${escapeHtml(f.airplaneModel)} <span class="cell-dim">${escapeHtml(f.registration)}</span></td>
      <td>${formatDate(f.departureTime)}</td>
      <td>${formatDate(f.arrivalTime)}</td>
      <td>${statusBadge(f.status)}</td>
      <td>
        <div class="row-actions">
          ${!canWrite() ? `<span class="cell-dim">—</span>` : `
          <button class="btn-outline" onclick="editFlight(${f.id})">Edit</button>
          <select class="select-inline" onchange="if(this.value){patchStatus('/flights/'+${f.id}+'/status',{status:this.value},[loadFlights]);this.value='';}">
            <option value="">Set status…</option>
            ${FLIGHT_STATUSES.filter(s => s !== f.status).map(s => `<option value="${s}">${s}</option>`).join("")}
          </select>
          ${isAdminUser() ? `<button class="btn-outline-red" onclick="deleteFlight(${f.id})">Delete</button>` : ``}
          `}
        </div>
      </td>
    </tr>
  `).join("");
}

async function loadFlights() {
  try {
    const page = await api.get("/flights?pageSize=200");
    renderFlightsTable(page.items || []);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterFlights() {
  const qs = buildQuery({ status: "ffStatus", fromDate: "ffFrom", toDate: "ffTo", originCode: "ffOrigin", destinationCode: "ffDest", availableOnly: "ffAvailable" });
  try {
    const data = await api.get(`/flights/filter${qs ? "?" + qs : ""}`);
    renderFlightsTable(data);
  } catch (err) { showAlert(err.message); }
}

function editFlight(id) {
  const f = flightRows.find(r => r.id === id);
  if (!f) return;
  document.getElementById("flightId").value = f.id;
  document.getElementById("flightNumber").value = f.flightNumber;
  document.getElementById("flightRouteId").value = f.routeId;
  // FlightUpdateDto has no RouteId - changing a flight's route after
  // creation isn't supported, so lock the field while editing
  document.getElementById("flightRouteId").disabled = true;
  document.getElementById("flightAirplaneId").value = f.airplaneId;
  document.getElementById("departureTime").value = f.departureTime.substring(0, 16);
  document.getElementById("arrivalTime").value = f.arrivalTime.substring(0, 16);
  window.scrollTo(0, 0);
}
async function deleteFlight(id) {
  if (!await uiConfirm("Flights with sold tickets cannot be deleted — cancel them instead.", { title: "Delete flight", confirmLabel: "Delete", danger: true })) return;
  try { await api.del(`/flights/${id}`); showAlert("Flight deleted.", "success"); loadFlights(); } catch (e) { showAlert(e.message); }
}

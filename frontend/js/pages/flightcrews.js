let rosterPager;

// Rosters (FlightCrew) page - who is working which flight.
// Depends on api.js, auth.js and common.js being loaded first.
//
// FlightCrewsController is Admin/Staff on every endpoint, so this whole page is
// staff-only - common.js PAGE_ROLES hides the nav item for a Passenger and the
// server rejects them anyway if they navigate here directly.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("flightcrew")) return;

  rosterPager = createPager({
    pageSize: 15,
    mountId: "rostersPager",
    onRender: renderRostersTable
  });

  loadPickers().then(loadRosters);

  document.getElementById("rosterForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("rosterId").value;
    const dutyRole = document.getElementById("rosterDutyRole").value;

    try {
      if (id) {
        // only the duty role is updatable - moving someone to a different
        // flight is a delete + create, see the note in the controller
        await api.put(`/flightcrews/${id}`, { dutyRole });
        showAlert("Duty role updated", "success");
      } else {
        await api.post("/flightcrews", {
          flightId: Number(document.getElementById("rosterFlightId").value),
          crewId: Number(document.getElementById("rosterCrewId").value),
          dutyRole
        });
        showAlert("Crew member assigned to flight", "success");
      }
      resetRosterForm();
      loadRosters();
    } catch (err) { showAlert(err.message); }
  });
});


// the create form needs real flights and real crew to pick from - typing raw
// FK ids is how you end up with a 400 for "Flight 9999 not found"
async function loadPickers() {
  try {
    const [flightsPage, crews] = await Promise.all([
      api.get("/flights?pageSize=200"),
      api.get("/crews")
    ]);
    const flights = flightsPage.items || [];

    document.getElementById("rosterFlightId").innerHTML = flights
      .filter(f => f.status !== "Cancelled")
      .map(f => `<option value="${f.id}">${escapeHtml(f.flightNumber)} · ` +
                `${escapeHtml(f.originCode)}→${escapeHtml(f.destinationCode)} · ` +
                `${formatDate(f.departureTime)}</option>`)
      .join("");

    document.getElementById("rosterCrewId").innerHTML = crews
      .map(c => `<option value="${c.id}">${escapeHtml(c.name)} · ${escapeHtml(c.role)} · ` +
                `${escapeHtml(c.licenseNumber)}</option>`)
      .join("");
  } catch (err) {
    if (err.status !== 403) showAlert(err.message);
  }
}


let rosterRows = [];

function renderRostersTable(data) {
  rosterRows = data;
  document.getElementById("rostersTable").innerHTML = data.map(r => `
    <tr>
      <td class="mono-link">${r.id}</td>
      <td><b>${escapeHtml(r.flightNumber)}</b> <span class="cell-dim">${escapeHtml(r.originCode)}→${escapeHtml(r.destinationCode)}</span></td>
      <td>${formatDate(r.departureTime)}</td>
      <td>${escapeHtml(r.crewName)} <span class="cell-dim">#${r.crewId}</span></td>
      <td><span class="cell-dim">${escapeHtml(r.crewRole)} · ${escapeHtml(r.licenseNumber)}</span></td>
      <td>${statusBadge(r.dutyRole)}</td>
      <td>${statusBadge(r.flightStatus)}</td>
      <td>
        <div class="row-actions">
          <button class="btn-outline" onclick="editRoster(${r.id})">Edit duty</button>
          <button class="btn-outline-red" onclick="deleteRoster(${r.id})">Remove</button>
        </div>
      </td>
    </tr>
  `).join("");
}


async function loadRosters() {
  try {
    rosterPager.setData(await api.get("/flightcrews"));
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}


async function filterRosters() {
  const qs = buildQuery({
    flightId: "ffcFlightId",
    crewId: "ffcCrewId",
    dutyRole: "ffcDutyRole",
    crewName: "ffcCrewName",
    fromDate: "ffcFromDate",
    toDate: "ffcToDate"
  });
  try {
    rosterPager.setData(await api.get(`/flightcrews/filter${qs}`));
  } catch (err) { showAlert(err.message); }
}


// only the duty role can change, so the flight and crew pickers are locked
// while editing - that mirrors what the API will actually accept
function editRoster(id) {
  const r = rosterRows.find(x => x.id === id);
  if (!r) return;

  document.getElementById("rosterId").value = r.id;
  document.getElementById("rosterFlightId").value = r.flightId;
  document.getElementById("rosterCrewId").value = r.crewId;
  document.getElementById("rosterDutyRole").value = r.dutyRole;
  document.getElementById("rosterFlightId").disabled = true;
  document.getElementById("rosterCrewId").disabled = true;

  window.scrollTo({ top: 0, behavior: "smooth" });
}


function resetRosterForm() {
  document.getElementById("rosterForm").reset();
  document.getElementById("rosterId").value = "";
  document.getElementById("rosterFlightId").disabled = false;
  document.getElementById("rosterCrewId").disabled = false;
}


async function deleteRoster(id) {
  if (!await uiConfirm("This takes the crew member off this flight's roster.", { title: "Remove assignment", confirmLabel: "Delete", danger: true })) return;
  try {
    await api.del(`/flightcrews/${id}`);
    showAlert("Assignment removed", "success");
    loadRosters();
  } catch (err) { showAlert(err.message); }
}

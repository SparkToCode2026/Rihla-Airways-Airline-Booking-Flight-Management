// Crew page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("crew")) return;
  loadCrew();

  document.getElementById("crewForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("crewId").value;
    const body = {
      name: document.getElementById("crewName").value,
      role: document.getElementById("crewRole").value,
      licenseNumber: document.getElementById("licenseNumber").value,
      passportNumber: document.getElementById("crewPassport").value,
      nationality: document.getElementById("crewNationality").value,
      dateOfBirth: document.getElementById("crewDob").value
    };
    try {
      if (id) await api.put(`/crews/${id}`, body);
      else await api.post("/crews", body);
      showAlert("Crew Member saved successfully", "success");
      document.getElementById("crewForm").reset();
      document.getElementById("crewId").value = "";
      loadCrew();
    } catch (err) { showAlert(err.message); }
  });
});

let crewRows = [];

function renderCrewTable(data) {
  crewRows = data;
  document.getElementById("crewTable").innerHTML = data.map(c => `
    <tr>
      <td class="mono-link">${c.id}</td>
      <td><b>${escapeHtml(c.name)}</b></td>
      <td>${escapeHtml(c.role)}</td>
      <td>${escapeHtml(c.licenseNumber)}</td>
      <td>${escapeHtml(c.nationality)}</td>
      <td>${c.totalAssignments}</td>
      <td>${c.upcomingFlights}</td>
      <td>
        <button class="btn-outline" onclick="editCrew(${c.id})">Edit</button>
        <button class="btn-outline-red" onclick="deleteCrew(${c.id})">Delete</button>
      </td>
    </tr>
  `).join("");
}

async function loadCrew() {
  try {
    // CrewsController -> "api/Crews", plural.
    // GetAll returns CrewSummaryDto for Admin/Staff - { id, name, role,
    // licenseNumber, nationality, totalAssignments, upcomingFlights }.
    // passportNumber and dateOfBirth are not in this shape at all
    const data = await api.get("/crews");
    renderCrewTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterCrew() {
  const qs = buildQuery({ role: "fcrRole", nationality: "fcrNationality", name: "fcrName", freeOn: "fcrFreeOn", availableOnly: "fcrAvailable" });
  try {
    const data = await api.get(`/crews/filter${qs ? "?" + qs : ""}`);
    renderCrewTable(data);
  } catch (err) { showAlert(err.message); }
}

// takes an id, not the row object - the list only has the redacted summary
// (no passport/DOB). GET /crews/{id} returns the full CrewDetailDto only for
// Admin; Staff get the summary again, so passport/DOB stay blank for them -
// matching that Crew editing is Admin-only on the backend regardless
async function editCrew(id) {
  try {
    const c = await api.get(`/crews/${id}`);
    document.getElementById("crewId").value = c.id;
    document.getElementById("crewName").value = c.name;
    document.getElementById("crewRole").value = c.role;
    document.getElementById("licenseNumber").value = c.licenseNumber;
    document.getElementById("crewPassport").value = c.passportNumber || "";
    document.getElementById("crewNationality").value = c.nationality;
    document.getElementById("crewDob").value = c.dateOfBirth || "";
    if (!c.passportNumber) showAlert("Only an Admin can edit crew records.", "warning");
    window.scrollTo(0, 0);
  } catch (err) { showAlert(err.message); }
}
async function deleteCrew(id) {
  if (!confirm("Delete crew member?")) return;
  try { await api.del(`/crews/${id}`); showAlert("Crew member deleted.", "success"); loadCrew(); } catch (e) { showAlert(e.message); }
}

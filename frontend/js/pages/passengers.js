// Passenger Profiles page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("passengers")) return;
  loadPassengers();

  document.getElementById("profileForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("profileId").value;
    const body = {
      userId: parseInt(document.getElementById("profileUserId").value),
      passportNumber: document.getElementById("passportNumber").value,
      nationality: document.getElementById("nationality").value,
      dateOfBirth: document.getElementById("dateOfBirth").value,
      phoneNumber: document.getElementById("phoneNumber").value
    };
    try {
      if (id) await api.put(`/passengerprofiles/${id}`, body);
      else await api.post("/passengerprofiles", body);
      showAlert("Passenger Profile saved successfully", "success");
      document.getElementById("profileForm").reset();
      document.getElementById("profileId").value = "";
      loadPassengers();
    } catch (err) { showAlert(err.message); }
  });
});

async function loadPassengers() {
  try {
    // PassengerProfilesController has no custom [Route] - the default
    // "api/[controller]" token resolves to "PassengerProfiles", not a
    // kebab-cased "passenger-profiles". routing is case-insensitive but
    // not hyphen-aware, so the hyphenated form 404s
    //
    // GetAll returns ProfileSummaryDto for Admin/Staff - a REDACTED shape:
    // { id, userId, userName, passportLast4, nationality, age }. It does NOT
    // include passportNumber, dateOfBirth, or phoneNumber - those only come
    // back from GET /passengerprofiles/{id} when the caller is the profile's
    // owner or an Admin.
    const data = await api.get("/passengerprofiles");
    renderPassengersTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

function renderPassengersTable(data) {
  document.getElementById("passengersTable").innerHTML = data.map(p => `
    <tr>
      <td class="mono-link">${p.id}</td>
      <td>${escapeHtml(p.userName)} <span class="cell-dim">(User #${p.userId})</span></td>
      <td>•••• ${escapeHtml(p.passportLast4)}</td>
      <td>${escapeHtml(p.nationality)}</td>
      <td>${p.age}</td>
      <td>
        <button class="btn-outline" onclick="editProfile(${p.id})">Edit</button>
        <button class="btn-outline-red" onclick="deleteProfile(${p.id})">Delete</button>
      </td>
    </tr>
  `).join("");
}

async function filterPassengers() {
  const qs = buildQuery({ nationality: "fpNationality", passportNumber: "fpPassport", minAge: "fpMinAge", maxAge: "fpMaxAge" });
  try {
    const data = await api.get(`/passengerprofiles/filter${qs ? "?" + qs : ""}`);
    renderPassengersTable(data);
  } catch (err) { showAlert(err.message); }
}

// takes an id, not the row object - the list only carries the redacted
// summary (no passportNumber/dateOfBirth/phoneNumber), so the full record
// has to be re-fetched. GET /passengerprofiles/{id} returns the full detail
// if you're the owner or an Admin; Staff who aren't either get the same
// redacted summary back and the form will end up blank for those fields -
// that's the backend enforcing "only the owner or Admin can edit this",
// which also means Save will 403 for anyone else, by design
async function editProfile(id) {
  try {
    const p = await api.get(`/passengerprofiles/${id}`);
    document.getElementById("profileId").value = p.id;
    document.getElementById("profileUserId").value = p.userId;
    document.getElementById("passportNumber").value = p.passportNumber || "";
    document.getElementById("nationality").value = p.nationality || "";
    document.getElementById("dateOfBirth").value = p.dateOfBirth || "";
    document.getElementById("phoneNumber").value = p.phoneNumber || "";
    if (!p.passportNumber) showAlert("You can only see and edit your own profile, or any profile if you're an Admin. This one is read-only for you.", "warning");
    window.scrollTo(0, 0);
  } catch (err) { showAlert(err.message); }
}
async function deleteProfile(id) {
  if (!confirm("Delete profile?")) return;
  try { await api.del(`/passengerprofiles/${id}`); showAlert("Profile deleted.", "success"); loadPassengers(); } catch (e) { showAlert(e.message); }
}

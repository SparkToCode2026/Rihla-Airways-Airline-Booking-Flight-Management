// My Profile. Depends on api.js, ui.js, auth.js and common.js.
//
// Everything here is scoped to the signed-in user by construction - it only
// ever calls /auth/me, /users/{ownId} and /passengerprofiles/{ownProfileId}.
// The API enforces that too: both of those are owner-or-admin.

let myProfileId = null;      // PassengerProfile.Id, null until we find one

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell(null)) return;   // no nav item to highlight - reached via the avatar
  loadProfile();

  document.getElementById("accountForm").addEventListener("submit", saveAccount);
  document.getElementById("profileForm").addEventListener("submit", saveTravelDoc);
});


async function loadProfile() {
  const me = auth.getUser() || {};

  // /auth/me rather than the cached localStorage copy - the cache is written
  // at login and goes stale the moment anything changes
  try {
    const acct = await api.get("/auth/me");
    document.getElementById("accName").value = acct.name || "";
    document.getElementById("accEmail").value = acct.email || "";
    document.getElementById("accRole").textContent = acct.role || "—";
    document.getElementById("accSince").textContent = formatDate(acct.createdAt);
  } catch (err) { showAlert(err.message); }

  // the travel-document card only makes sense for passengers - staff and
  // admins have no passport on file, that's the whole reason the table is
  // separate from User
  if (me.role !== "Passenger") {
    document.getElementById("profileCard").remove();
  } else {
    await loadTravelDoc();
  }

  await loadMyBookings();
}


// GET /passengerprofiles/me resolves the caller's own profile server-side.
// profile ids don't match user ids and the list endpoint is staff-only, so
// without that route this would have to probe /1, /2, /3... until one
// returned 200. it answers 204 when the user hasn't created one yet
async function loadTravelDoc() {
  const form = document.getElementById("profileForm");
  const empty = document.getElementById("profileEmpty");

  try {
    const p = await api.get("/passengerprofiles/me");

    if (!p) {                       // 204 No Content -> nothing on file yet
      empty.style.display = "block";
      document.getElementById("pfSubmit").textContent = "Add details";
      form.reset();
      return;
    }

    myProfileId = p.id;
    document.getElementById("pfPassport").value = p.passportNumber || "";
    document.getElementById("pfNationality").value = p.nationality || "";
    document.getElementById("pfDob").value = (p.dateOfBirth || "").slice(0, 10);
    document.getElementById("pfPhone").value = p.phoneNumber || "";
    document.getElementById("pfSubmit").textContent = "Update details";
  } catch (err) {
    showAlert(err.message);
  }
}


async function saveAccount(e) {
  e.preventDefault();
  const me = auth.getUser() || {};
  const body = {
    name: document.getElementById("accName").value.trim(),
    email: document.getElementById("accEmail").value.trim()
  };
  try {
    await api.put(`/users/${me.userId}`, body);
    // keep the cached copy in step or the sidebar keeps showing the old name
    localStorage.setItem("rihla_user", JSON.stringify({ ...me, ...body }));
    uiToast("Account updated", "success");
    document.getElementById("sidebarName").textContent = body.name;
    document.getElementById("sidebarAvatar").textContent =
      (body.name[0] || "?").toUpperCase();
  } catch (err) { showAlert(err.message); }
}


async function saveTravelDoc(e) {
  e.preventDefault();
  const me = auth.getUser() || {};
  const body = {
    passportNumber: document.getElementById("pfPassport").value.trim(),
    nationality: document.getElementById("pfNationality").value.trim(),
    dateOfBirth: document.getElementById("pfDob").value,
    phoneNumber: document.getElementById("pfPhone").value.trim()
  };

  try {
    if (myProfileId) {
      await api.put(`/passengerprofiles/${myProfileId}`, body);
      uiToast("Travel details updated", "success");
    } else {
      // UserId is only on the CREATE dto - it's not updatable, because moving
      // a profile between users would break the 1:1
      const created = await api.post("/passengerprofiles", { userId: me.userId, ...body });
      myProfileId = created.id;
      document.getElementById("profileEmpty").style.display = "none";
      document.getElementById("pfSubmit").textContent = "Update details";
      uiToast("Travel details saved", "success");
    }
  } catch (err) { showAlert(err.message); }
}


async function loadMyBookings() {
  try {
    const rows = await api.get("/bookings");     // already scoped server-side
    document.getElementById("profileBookings").innerHTML = rows.length
      ? rows.slice(0, 8).map(b => `
        <tr>
          <td class="mono-link">${b.id}</td>
          <td>${formatDate(b.bookingDate)}</td>
          <td><b>${formatMoney(b.totalAmount)}</b></td>
          <td>${statusBadge(b.status)}</td>
          <td><span class="cell-dim">${b.isPaid ? escapeHtml(b.paymentStatus) : "unpaid"}</span></td>
        </tr>`).join("")
      : `<tr><td colspan="5" class="cell-dim">No bookings yet.</td></tr>`;
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

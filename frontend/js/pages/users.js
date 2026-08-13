// Users page. Depends on api.js, auth.js and common.js being loaded first.

document.addEventListener("DOMContentLoaded", () => {
  if (!initShell("users")) return;
  loadUsers();

  // UserUpdateDto only accepts { name, email } - role goes through
  // PATCH /users/{id}/role (see the row action) and there is no
  // password-reset endpoint at all, so editing never sends either
  document.getElementById("userForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const id = document.getElementById("userId").value;
    try {
      if (id) {
        await api.put(`/users/${id}`, {
          name: document.getElementById("userName").value,
          email: document.getElementById("userEmail").value
        });
      } else {
        await api.post("/users", {
          name: document.getElementById("userName").value,
          email: document.getElementById("userEmail").value,
          password: document.getElementById("userPassword").value,
          role: document.getElementById("userRole").value
        });
      }
      showAlert("User saved successfully", "success");
      document.getElementById("userForm").reset();
      document.getElementById("userId").value = "";
      resetUserFormMode();
      loadUsers();
    } catch (err) { showAlert(err.message); }
  });
});

const USER_ROLES = ["Passenger", "Staff", "Admin"];
let userRows = [];

function renderUsersTable(data) {
  userRows = data;
  document.getElementById("usersTable").innerHTML = data.map(u => `
    <tr>
      <td class="mono-link">${u.id}</td>
      <td><b>${escapeHtml(u.name)}</b></td>
      <td>${escapeHtml(u.email)}</td>
      <td>${statusBadge(u.role)}</td>
      <td>
        <div class="row-actions">
          <button class="btn-outline" onclick="editUser(${u.id})">Edit</button>
          <select class="form-control" style="width:auto;display:inline-block;padding:4px 6px;" onchange="if(this.value){patchStatus('/users/'+${u.id}+'/role',{role:this.value},[loadUsers]);this.value='';}">
            <option value="">Change role…</option>
            ${USER_ROLES.filter(r => r !== u.role).map(r => `<option value="${r}">${r}</option>`).join("")}
          </select>
          <button class="btn-outline-red" onclick="deleteUser(${u.id})">Delete</button>
        </div>
      </td>
    </tr>
  `).join("");
}

async function loadUsers() {
  try {
    const data = await api.get("/users");
    renderUsersTable(data);
  } catch (err) { if (err.status !== 403) showAlert(err.message); }
}

async function filterUsers() {
  const qs = buildQuery({ role: "fuRole", email: "fuEmail", registeredAfter: "fuAfter" });
  try {
    const data = await api.get(`/users/filter${qs ? "?" + qs : ""}`);
    renderUsersTable(data);
  } catch (err) { showAlert(err.message); }
}

function editUser(id) {
  const u = userRows.find(r => r.id === id);
  if (!u) return;
  document.getElementById("userId").value = u.id;
  document.getElementById("userName").value = u.name;
  document.getElementById("userEmail").value = u.email;
  // password and role aren't part of UserUpdateDto - hide them while editing
  // so the form doesn't imply a change that silently won't happen
  document.getElementById("userPasswordGroup").style.display = "none";
  document.getElementById("userRoleGroup").style.display = "none";
  document.getElementById("userPassword").required = false;
  document.getElementById("userFormBtn").textContent = "Update User";
  window.scrollTo(0, 0);
}
function resetUserFormMode() {
  document.getElementById("userPasswordGroup").style.display = "";
  document.getElementById("userRoleGroup").style.display = "";
  document.getElementById("userPassword").required = false;
  document.getElementById("userFormBtn").textContent = "Create User";
}
async function deleteUser(id) {
  if (!confirm("Delete user?")) return;
  try { await api.del(`/users/${id}`); showAlert("User deleted.", "success"); loadUsers(); } catch (e) { showAlert(e.message); }
}

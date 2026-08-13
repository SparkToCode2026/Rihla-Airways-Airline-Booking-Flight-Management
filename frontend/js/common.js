// ============================================================
// Shared shell + helpers for every authenticated app page
// (dashboard, flights, airports, routes, bookings, tickets,
// passengers, payments, baggage, seatclasses, airplanes, crew,
// users). Each of those pages has its own <html> file and its
// own js/pages/<name>.js with just that entity's load/render/
// filter/edit/delete logic - this file is everything they all
// need in common, so it isn't copy-pasted 13 times:
//   - requireLogin guard + sidebar user info + role-based nav hiding
//   - active nav-item highlighting (based on the current filename)
//   - sidebar collapse toggle
//   - the alert dismiss button wiring (api.js's showAlert markup
//     uses data-bs-dismiss, which needs Bootstrap's JS - these pages
//     don't load Bootstrap, so it's wired up here instead)
//   - the API health-check pill
//   - patchStatus() - the shared PATCH helper for every state-machine
//     endpoint (flight/booking/payment status, user role)
//   - buildQuery / clearFilter / loadStats / renderStatsValue /
//     formatStatCell / humanizeKey - the filter+stats helpers used
//     by all 12 CRUD pages' "case 7 / case 8" UI
//
// depends on api.js being loaded first (auth, api, showAlert,
// escapeHtml, formatDate, formatMoney, statusBadge, requireLogin)
// and auth.js for logout(). include both before this file.
// ============================================================

// which nav items are hidden for which roles. cosmetic only - the
// backend still enforces [Authorize(Roles=...)] on every endpoint
// regardless of whether the link is visible, this just stops
// someone clicking into a page that's guaranteed to 403 for them
const PAGE_ROLES = {
  passengers: ["Admin", "Staff"],
  payments: ["Admin", "Staff"],
  baggage: ["Admin", "Staff"],
  airplanes: ["Admin", "Staff"],
  crew: ["Admin", "Staff"],
  users: ["Admin"]
};

// call once at the top of every app page's DOMContentLoaded handler,
// passing the page's own key (matches the nav-item's data-page and
// the PAGE_ROLES keys above). returns false if the user got bounced
// to the login page, so the caller can bail out early
function initShell(pageKey) {
  if (!requireLogin()) return false;

  const me = auth.getUser();
  if (me) {
    const avatarEl = document.getElementById("sidebarAvatar");
    const nameEl = document.getElementById("sidebarName");
    const roleEl = document.getElementById("sidebarRole");
    if (avatarEl) avatarEl.textContent = me.name ? me.name.charAt(0).toUpperCase() : "?";
    if (nameEl) nameEl.textContent = me.name || "—";
    if (roleEl) roleEl.textContent = me.role || "—";

    document.querySelectorAll(".nav-item").forEach(item => {
      const allowed = PAGE_ROLES[item.dataset.page];
      if (allowed && !allowed.includes(me.role)) item.style.display = "none";
    });
  }

  document.querySelectorAll(".nav-item").forEach(item => {
    item.classList.toggle("active", item.dataset.page === pageKey);
  });

  const collapseBtn = document.getElementById("collapseBtn");
  if (collapseBtn) {
    collapseBtn.addEventListener("click", () => {
      document.getElementById("sidebar").classList.toggle("collapsed");
    });
  }

  const alertBox = document.getElementById("alert");
  if (alertBox) {
    alertBox.addEventListener("click", (e) => {
      if (e.target.closest(".btn-close")) alertBox.innerHTML = "";
    });
  }

  checkApiStatus();
  return true;
}

/* ============================================================
   API HEALTH CHECK
   the "Connected" pill used to be hardcoded and never actually
   checked anything. this fires one lightweight GET on load and
   flips it red on failure
   ============================================================ */
async function checkApiStatus() {
  const pill = document.getElementById("apiStatusPill");
  if (!pill) return;
  try {
    await api.get("/airports");
    pill.className = "pill pill-api";
    pill.innerHTML = '<span class="pill-dot"></span>Connected';
  } catch (err) {
    pill.className = "pill pill-api-down";
    pill.innerHTML = '<span class="pill-dot"></span>API unreachable';
  }
}

// PATCH helper for the state-machine endpoints (flight/booking/payment
// status, user role). optional confirm text since some of these are
// destructive-ish
async function patchStatus(path, body, reloadFns, confirmText) {
  if (confirmText && !confirm(confirmText)) return;
  try {
    await api.patch(path, body);
    showAlert("Updated successfully", "success");
    reloadFns.forEach(fn => fn());
  } catch (err) { showAlert(err.message); }
}

/* ============================================================
   FILTER / STATS HELPERS
   every controller implements GET /filter and GET /stats (cases 7
   and 8 of the CRUD spec) - these helpers are what let each page
   wire those up in a few lines instead of hand-building query
   strings and bespoke stats markup twelve separate times
   ============================================================ */

// reads a {queryParam: elementId} map, skips anything blank/unset,
// returns a query string with no leading "?". works for text/number/
// date inputs and checkboxes alike
function buildQuery(fieldMap) {
  const params = new URLSearchParams();
  for (const [key, id] of Object.entries(fieldMap)) {
    const el = document.getElementById(id);
    if (!el) continue;
    const value = el.type === "checkbox" ? el.checked : el.value;
    if (value === "" || value === false || value === null || value === undefined) continue;
    params.set(key, value);
  }
  return params.toString();
}

// resets a filter form's fields and reloads the unfiltered list
function clearFilter(ids, reloadFn) {
  ids.forEach(id => {
    const el = document.getElementById(id);
    if (!el) return;
    if (el.type === "checkbox") el.checked = false; else el.value = "";
  });
  reloadFn();
}

// every /stats endpoint returns a DIFFERENT shape - some a flat array
// (SeatClasses, Tickets), some an object with several named sections
// (Airports: byCountry / busiestHubs / unusedAirports; Bookings:
// byStatus / topCustomers / integrityCheck). rather than hand-build
// bespoke markup for each of the 12, this walks whatever comes back:
// objects become labelled sections, arrays of objects become mini
// tables, arrays of scalars become a comma list, plain values are
// shown as-is
async function loadStats(path, containerId) {
  const box = document.getElementById(containerId);
  box.innerHTML = '<span class="cell-dim">Loading…</span>';
  try {
    const data = await api.get(path);
    box.innerHTML = renderStatsValue(data);
  } catch (err) {
    box.innerHTML = "";
    showAlert(err.message);
  }
}
function renderStatsValue(val) {
  if (val === null || val === undefined) return '<span class="cell-dim">—</span>';
  if (Array.isArray(val)) {
    if (val.length === 0) return '<span class="cell-dim">No rows.</span>';
    if (typeof val[0] === "object" && val[0] !== null) {
      const cols = Object.keys(val[0]);
      return `<table><thead><tr>${cols.map(c => `<th>${escapeHtml(humanizeKey(c))}</th>`).join("")}</tr></thead>
        <tbody>${val.map(row => `<tr>${cols.map(c => `<td>${formatStatCell(row[c])}</td>`).join("")}</tr>`).join("")}</tbody></table>`;
    }
    return escapeHtml(val.join(", "));
  }
  if (typeof val === "object") {
    return Object.entries(val).map(([key, v]) => `
      <div class="stat-section">
        <h4>${escapeHtml(humanizeKey(key))}</h4>
        ${renderStatsValue(v)}
      </div>
    `).join("");
  }
  return escapeHtml(String(val));
}
function formatStatCell(v) {
  if (v === null || v === undefined) return '<span class="cell-dim">—</span>';
  if (typeof v === "object") return renderStatsValue(v);
  if (typeof v === "boolean") return v ? "Yes" : "No";
  return escapeHtml(String(v));
}
// "TotalBookings" -> "Total Bookings", "byStatus" -> "By Status"
function humanizeKey(key) {
  return key.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, s => s.toUpperCase()).trim();
}

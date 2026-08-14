// ============================================================
// the sidebar + topbar that every page renders. one file so
// adding a nav item means editing HERE, not in 15 html files.
//
// each page calls renderLayout("airports") on load and the
// active state, user block and sign-out all come for free
// ============================================================

// nav items in sidebar order. `key` is what a page passes in to
// mark itself active. `roles` limits visibility - null means
// everyone, otherwise only those roles see the link.
// note this is COSMETIC: hiding a link doesn't protect the
// endpoint, the server checks roles on every request. it just
// stops staff clicking through to a guaranteed 403
const NAV = [
  { key: "dashboard",  label: "Dashboard",   icon: "▦", href: "index.html",            roles: null },
  { key: "flights",    label: "Flights",     icon: "✈", href: "flights.html",          roles: null },
  { key: "airports",   label: "Airports",    icon: "◉", href: "airports.html",         roles: null },
  { key: "routes",     label: "Routes",      icon: "⟷", href: "routes.html",           roles: null },
  { key: "bookings",   label: "Bookings",    icon: "≡", href: "bookings.html",         roles: null },
  { key: "tickets",    label: "Tickets",     icon: "◈", href: "tickets.html",          roles: null },
  // these hrefs must match the real filenames in /html. three of them pointed
  // at passengerprofiles.html / baggages.html / crews.html, which don't exist -
  // the pages are named after the PAGE, not the API resource. four dead links
  // in the admin sidebar came from that mismatch
  { key: "passengers", label: "Passengers",  icon: "◎", href: "passengers.html",       roles: ["Admin", "Staff"] },
  { key: "payments",   label: "Payments",    icon: "$", href: "payments.html",         roles: ["Admin", "Staff"] },
  { key: "baggage",    label: "Baggage",     icon: "▤", href: "baggage.html",          roles: ["Admin", "Staff"] },
  { key: "seatclasses",label: "Seat Classes",icon: "⊞", href: "seatclasses.html",      roles: null },
  { key: "airplanes",  label: "Airplanes",   icon: "◆", href: "airplanes.html",        roles: ["Admin", "Staff"] },
  { key: "crew",       label: "Crew",        icon: "★", href: "crew.html",             roles: ["Admin", "Staff"] },
  { key: "flightcrew", label: "Rosters",     icon: "⚑", href: "flightcrews.html",      roles: ["Admin", "Staff"] },
  { key: "users",      label: "Users",       icon: "•", href: "users.html",            roles: ["Admin"] }
];


// call this FIRST on every page. it bounces to login if there's
// no token, so nothing else runs for a signed-out visitor
function renderLayout(activeKey, breadcrumb) {
  if (!requireLogin()) return false;

  const user = auth.getUser();

  const links = NAV
    .filter(item => !item.roles || item.roles.includes(user.role))
    .map(item => `
      <a href="${item.href}" class="ra-nav-item ${item.key === activeKey ? "active" : ""}">
        <span class="ra-nav-icon">${item.icon}</span>
        <span>${item.label}</span>
      </a>`)
    .join("");

  // escapeHtml on the name because it comes from the database -
  // a user called <img onerror=...> would otherwise execute here.
  // the initial is just the first letter for the avatar circle
  const initial = escapeHtml(user.name.charAt(0).toUpperCase());

  document.getElementById("layout").innerHTML = `
    <aside class="ra-sidebar">
      <a href="index.html" class="ra-brand">
        <span class="ra-logo">✈</span>
        <span class="ra-brand-text">Rihla</span>
      </a>

      <nav class="ra-nav">${links}</nav>

      <div class="ra-user">
        <div class="ra-avatar">${initial}</div>
        <div class="ra-user-meta">
          <div class="ra-user-name">${escapeHtml(user.name)}</div>
          <div class="ra-user-role">${escapeHtml(user.role)}</div>
        </div>
      </div>
    </aside>

    <div class="ra-main">
      <header class="ra-topbar">
        <div class="ra-crumb">
          <span class="text-muted">Rihla</span>
          <span class="text-muted">/</span>
          <span>${escapeHtml(breadcrumb)}</span>
        </div>
        <div class="d-flex align-items-center gap-2">
          <span id="apiStatus" class="ra-pill ra-pill-checking">● Checking…</span>
          <button class="btn btn-sm ra-btn-signout" onclick="logout()">Sign out</button>
        </div>
      </header>

      <main class="ra-content" id="content"></main>
    </div>`;

  checkApiStatus();
  return true;
}


// the "API Connected" pill from the design. it's not decoration -
// when a teammate opens the page with the backend stopped, this
// tells them why nothing loaded instead of leaving them staring
// at an empty table
async function checkApiStatus() {
  const pill = document.getElementById("apiStatus");
  try {
    await api.get("/seatclasses");
    pill.className = "ra-pill ra-pill-ok";
    pill.textContent = "● API Connected";
  } catch {
    pill.className = "ra-pill ra-pill-bad";
    pill.textContent = "● API Offline";
  }
}


// ---------- page building blocks ----------
// every model page has the same header shape, so it lives here

function pageHeader(title, subtitle, actionLabel, actionFn) {
  const btn = actionLabel
    ? `<button class="btn btn-primary" onclick="${actionFn}">+ ${escapeHtml(actionLabel)}</button>`
    : "";

  return `
    <div class="d-flex justify-content-between align-items-start mb-4">
      <div>
        <h1 class="ra-title">${escapeHtml(title)}</h1>
        <p class="text-muted mb-0">${escapeHtml(subtitle)}</p>
      </div>
      ${btn}
    </div>`;
}

// the stat cards across the top of bookings/dashboard.
// value is NOT escaped - it's often html we built ourselves
// (a coloured number), and it never comes from user input
function statCard(label, value, note = "", tone = "") {
  return `
    <div class="col">
      <div class="ra-stat">
        <div class="ra-stat-label">${escapeHtml(label)}</div>
        <div class="ra-stat-value ${tone}">${value}</div>
        ${note ? `<div class="ra-stat-note">${escapeHtml(note)}</div>` : ""}
      </div>
    </div>`;
}

// shown while a fetch is in flight. without it the page is blank
// for a second and looks broken
function loadingRow(colspan) {
  return `<tr><td colspan="${colspan}" class="text-center text-muted py-5">
    <span class="spinner-border spinner-border-sm me-2"></span>Loading…
  </td></tr>`;
}

// an empty table with no message looks like a bug. this says
// "there's nothing here" rather than "something failed"
function emptyRow(colspan, message = "No records found.") {
  return `<tr><td colspan="${colspan}" class="text-center text-muted py-5">
    ${escapeHtml(message)}
  </td></tr>`;
}
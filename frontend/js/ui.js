// ============================================================
// Shared in-app UI components. Load AFTER api.js (it uses
// escapeHtml) and BEFORE common.js and the page scripts.
//
// Everything here exists to replace the browser's native
// window.confirm / window.prompt dialogs. Those are functionally
// fine but they're rendered by the OS, so they ignore the app's
// dark theme entirely, can't be styled, and look nothing like the
// rest of the product - a jarring grey box in the middle of a
// carefully themed page.
//
//   uiConfirm(...)  -> Promise<boolean>   replaces confirm()
//   uiPrompt(...)   -> Promise<string|null> replaces prompt()
//   uiSelect(...)   -> Promise<string|null> a picker with options
//   uiToast(...)                          transient corner message
//
// all three dialogs return promises, so callers become:
//   if (!await uiConfirm("Delete?")) return;
// which reads almost the same as the native version it replaced
// ============================================================

// one host element reused by every dialog, created on first use
function uiDialogHost() {
  let host = document.getElementById("uiDialogHost");
  if (!host) {
    host = document.createElement("div");
    host.id = "uiDialogHost";
    document.body.appendChild(host);
  }
  return host;
}


// the shared engine. `body` is the markup between title and buttons,
// `resolveValue` reads whatever the user entered back out of the DOM
// `onOpen` runs once the dialog markup is in the DOM, for bodies that need to
// be populated asynchronously - the seat map is drawn that way, since it has
// to fetch the flight's occupied seats before it can render anything
function uiDialog({ title, message, body = "", confirmLabel = "Confirm",
                    cancelLabel = "Cancel", danger = false, resolveValue = null,
                    onOpen = null }) {
  return new Promise(resolve => {
    const host = uiDialogHost();
    host.innerHTML = `
      <div class="ui-overlay" id="uiOverlay">
        <div class="ui-modal" role="dialog" aria-modal="true" aria-labelledby="uiModalTitle">
          <div class="ui-modal-head">
            <h3 id="uiModalTitle">${escapeHtml(title)}</h3>
          </div>
          <div class="ui-modal-body">
            ${message ? `<p class="ui-modal-msg">${escapeHtml(message)}</p>` : ""}
            ${body}
          </div>
          <div class="ui-modal-foot">
            <button type="button" class="btn-outline" id="uiCancel">${escapeHtml(cancelLabel)}</button>
            <button type="button" class="${danger ? "btn-danger" : "btn"}" id="uiOk">${escapeHtml(confirmLabel)}</button>
          </div>
        </div>
      </div>`;

    const overlay = document.getElementById("uiOverlay");

    const close = (value) => {
      document.removeEventListener("keydown", onKey);
      host.innerHTML = "";
      resolve(value);
    };

    // esc cancels, enter accepts - same as the native dialog people expect
    const onKey = (e) => {
      if (e.key === "Escape") close(resolveValue ? null : false);
      if (e.key === "Enter" && e.target.tagName !== "SELECT") {
        e.preventDefault();
        document.getElementById("uiOk").click();
      }
    };
    document.addEventListener("keydown", onKey);

    document.getElementById("uiOk").addEventListener("click", () => {
      close(resolveValue ? resolveValue() : true);
    });
    document.getElementById("uiCancel").addEventListener("click", () =>
      close(resolveValue ? null : false));

    // clicking the backdrop cancels, clicking the panel does not
    overlay.addEventListener("mousedown", (e) => {
      if (e.target === overlay) close(resolveValue ? null : false);
    });

    // focus whatever the user is meant to interact with first
    const focusMe = host.querySelector("input:not([type=hidden]), select")
                 || document.getElementById("uiOk");
    if (focusMe) focusMe.focus();

    if (onOpen) onOpen();
  });
}


function uiConfirm(message, { title = "Please confirm", confirmLabel = "Confirm",
                              cancelLabel = "Cancel", danger = false } = {}) {
  return uiDialog({ title, message, confirmLabel, cancelLabel, danger });
}


function uiPrompt(message, { title = "Enter a value", value = "", placeholder = "",
                             confirmLabel = "Save" } = {}) {
  return uiDialog({
    title, message, confirmLabel,
    body: `<input type="text" id="uiPromptInput" class="form-control"
                  value="${escapeHtml(value)}" placeholder="${escapeHtml(placeholder)}">`,
    resolveValue: () => document.getElementById("uiPromptInput").value
  });
}


// options: [{ value, label }]
function uiSelect(message, options, { title = "Choose an option", value = "",
                                      confirmLabel = "Confirm" } = {}) {
  return uiDialog({
    title, message, confirmLabel,
    body: `<select id="uiSelectInput" class="form-control">
             ${options.map(o => `<option value="${escapeHtml(o.value)}"
               ${o.value === value ? "selected" : ""}>${escapeHtml(o.label)}</option>`).join("")}
           </select>`,
    resolveValue: () => document.getElementById("uiSelectInput").value
  });
}


// transient corner message. showAlert() puts a banner at the top of the
// page, which is right for form errors you want to sit and read; this is
// for "saved" style confirmations that shouldn't push the layout around
function uiToast(message, type = "success", ms = 3200) {
  let stack = document.getElementById("uiToastStack");
  if (!stack) {
    stack = document.createElement("div");
    stack.id = "uiToastStack";
    stack.className = "ui-toast-stack";
    document.body.appendChild(stack);
  }

  const el = document.createElement("div");
  el.className = `ui-toast ui-toast-${type}`;
  el.innerHTML = `<span>${escapeHtml(message)}</span>`;
  stack.appendChild(el);

  // let the element land in the DOM before animating, otherwise the
  // transition has nothing to move from
  requestAnimationFrame(() => el.classList.add("is-in"));

  setTimeout(() => {
    el.classList.remove("is-in");
    setTimeout(() => el.remove(), 220);
  }, ms);
}

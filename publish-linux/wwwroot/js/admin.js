document.querySelectorAll('[data-copy]').forEach(function (btn) {
  btn.addEventListener('click', function () {
    var input = document.getElementById(btn.getAttribute('data-copy'));
    input.select();
    var done = function () { btn.textContent = 'Скопировано'; };
    if (navigator.clipboard) { navigator.clipboard.writeText(input.value).then(done); }
    else { document.execCommand('copy'); done(); }
  });
});

// Copy buttons for one-time links: the URL lives in data-copy-text and goes straight to the clipboard.
// No request is made to it and nothing is persisted anywhere in the browser.
document.querySelectorAll('[data-copy-text]').forEach(function (btn) {
  btn.addEventListener('click', function () {
    var text = btn.getAttribute('data-copy-text');
    var done = function () { var old = btn.textContent; btn.textContent = 'Скопировано'; setTimeout(function () { btn.textContent = old; }, 1500); };
    if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(text).then(done); return; }
    var area = document.createElement('textarea');
    area.value = text; area.setAttribute('readonly', ''); area.style.position = 'fixed'; area.style.opacity = '0';
    document.body.appendChild(area); area.select();
    try { document.execCommand('copy'); done(); } finally { document.body.removeChild(area); }
  });
});

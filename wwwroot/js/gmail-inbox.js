var gmCurrent = null;

// Reads all values from data-* attributes — no quote escaping issues
function openEmailFromRow(btn) {
    openEmail(
        btn.dataset.msgid,
        btn.dataset.threadid,
        btn.dataset.from || '',
        btn.dataset.to || '',
        btn.dataset.subject || '',
        btn.dataset.date || ''
    );
}

function filterEmails(q, btn) {
    if (btn) {
        document.querySelectorAll('.gm-nav-btn').forEach(function (b) { b.classList.remove('active'); });
        btn.classList.add('active');
    }
    window.location.href = '/Gmail/Inbox?q=' + encodeURIComponent(q || 'in:inbox');
}

function openEmail(msgId, threadId, from, to, subject, date) {
    document.querySelectorAll('.gm-row').forEach(function (r) { r.classList.remove('active'); });
    var row = document.getElementById('row-' + msgId);
    if (row) row.classList.add('active');

    gmCurrent = { msgId: msgId, threadId: threadId, from: from, subject: subject };

    document.getElementById('gmReadEmpty').style.display = 'none';
    document.getElementById('gmReadCard').classList.add('open');
    document.getElementById('gmSubject').textContent = subject || '(no subject)';
    document.getElementById('gmFrom').textContent = from || '';
    document.getElementById('gmTo').textContent = to || '';
    document.getElementById('gmDate').textContent = date || '';
    document.getElementById('gmBody').innerHTML = '<div class="gm-read-loading">Loading\u2026</div>';
    document.getElementById('gmReplyArea').style.display = 'none';
    document.getElementById('gmReplyText').value = '';
    document.getElementById('gmReplySend').disabled = true;

    fetch('/Gmail/Read/' + encodeURIComponent(msgId), {
        headers: { 'X-Requested-With': 'XMLHttpRequest' }
    })
        .then(function (r) {
            if (!r.ok) throw new Error('Server error ' + r.status);
            return r.json();
        })
        .then(function (data) {
            var bodyEl = document.getElementById('gmBody');

            // Update From/To/Date from server response (more accurate)
            if (data.from) document.getElementById('gmFrom').textContent = data.from;
            if (data.to) document.getElementById('gmTo').textContent = data.to;
            if (data.date) document.getElementById('gmDate').textContent = data.date;
            if (data.subject) document.getElementById('gmSubject').textContent = data.subject;

            if (!data.success) {
                bodyEl.innerHTML = '<p style="color:#c62828;padding:12px;font-size:13px;">\u26a0\ufe0f ' + (data.message || 'Could not load email.') + '</p>';
                return;
            }

            var html = (data.body || '').trim();
            if (!html) {
                bodyEl.innerHTML = '<p style="color:#8a97a8;padding:12px;font-size:13px;">(No message body)</p>';
                return;
            }

            bodyEl.innerHTML = '';
            var iframe = document.createElement('iframe');
            iframe.setAttribute('sandbox', 'allow-same-origin allow-popups allow-scripts');
            iframe.style.cssText = 'width:100%;border:none;min-height:200px;display:block;';
            bodyEl.appendChild(iframe);

            try {
                var doc = iframe.contentDocument || iframe.contentWindow.document;
                doc.open();
                doc.write('<!DOCTYPE html><html><head><style>body{margin:0;padding:12px;font-family:Arial,sans-serif;font-size:14px;word-wrap:break-word;line-height:1.5;}img{max-width:100%;}</style></head><body>' + html + '</body></html>');
                doc.close();
                iframe.onload = function () {
                    try {
                        var h = doc.documentElement.scrollHeight || doc.body.scrollHeight;
                        iframe.style.height = Math.max(h + 16, 200) + 'px';
                    } catch (e) { iframe.style.height = '400px'; }
                };
            } catch (e) {
                iframe.srcdoc = html;
                iframe.style.height = '400px';
            }

            setTimeout(function () {
                if (!iframe.style.height || parseInt(iframe.style.height) < 50)
                    iframe.style.height = '400px';
            }, 2000);
        })
        .catch(function (err) {
            document.getElementById('gmBody').innerHTML = '<p style="color:#c62828;padding:12px;font-size:13px;">\u26a0\ufe0f ' + err.message + '</p>';
        });
}

function closeEmail() {
    document.getElementById('gmReadCard').classList.remove('open');
    document.getElementById('gmReadEmpty').style.display = '';
    document.querySelectorAll('.gm-row').forEach(function (r) { r.classList.remove('active'); });
    gmCurrent = null;
}

function toggleReply() {
    var area = document.getElementById('gmReplyArea');
    var isHidden = area.style.display === 'none' || area.style.display === '';
    area.style.display = isHidden ? 'block' : 'none';
    if (isHidden) setTimeout(function () { document.getElementById('gmReplyText').focus(); }, 50);
}

function sendReply() {
    if (!gmCurrent) return;
    var text = document.getElementById('gmReplyText').value.trim();
    if (!text) return;
    var btn = document.getElementById('gmReplySend');
    btn.disabled = true;
    btn.textContent = 'Sending\u2026';

    fetch('/Gmail/Reply', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            threadId: gmCurrent.threadId,
            messageId: gmCurrent.msgId,
            to: gmCurrent.from,
            subject: gmCurrent.subject,
            replyBody: '<p>' + text.replace(/\n/g, '<br>') + '</p>'
        })
    })
        .then(function (r) { return r.json(); })
        .then(function (d) {
            if (d.success) {
                document.getElementById('gmReplyArea').style.display = 'none';
                document.getElementById('gmReplyText').value = '';
                alert('Reply sent!');
            } else {
                alert(d.message || 'Reply failed.');
            }
            btn.disabled = false;
            btn.textContent = 'Send';
        })
        .catch(function (e) {
            alert('Send failed: ' + e.message);
            btn.disabled = false;
            btn.textContent = 'Send';
        });
}

function openCompose() {
    document.getElementById('gmComposeModal').classList.add('open');
    document.getElementById('cTo').focus();
}

function closeCompose() {
    document.getElementById('gmComposeModal').classList.remove('open');
    ['cTo', 'cSubject', 'cBody'].forEach(function (id) {
        document.getElementById(id).value = '';
    });
}

function sendCompose() {
    var to = document.getElementById('cTo').value.trim();
    var subject = document.getElementById('cSubject').value.trim();
    var body = document.getElementById('cBody').value.trim();
    if (!to || !subject || !body) { alert('Please fill all fields.'); return; }

    fetch('/Gmail/Compose', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
            To: to,
            Subject: subject,
            BodyHtml: '<p>' + body.replace(/\n/g, '<br>') + '</p>'
        })
    })
        .then(function (r) { return r.json(); })
        .then(function (d) {
            if (d.success) { closeCompose(); alert('Email sent!'); }
            else alert(d.message || 'Send failed.');
        })
        .catch(function (e) { alert('Send failed: ' + e.message); });
}

function disconnectGmail() {
    if (!confirm('Disconnect Gmail?')) return;
    fetch('/Gmail/Disconnect', { method: 'POST' })
        .then(function (r) { return r.json(); })
        .then(function (d) { if (d.success) window.location.href = '/Dashboard'; });
}
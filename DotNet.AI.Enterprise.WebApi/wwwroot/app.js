// HR Assistant test UI — plain JS, no build step.
// The server keeps the chat history; the browser only remembers the conversation id.

const $ = (id) => document.getElementById(id);
const messagesEl = $("messages");
const inputEl = $("input");
const sendBtn = $("send");

let conversationId = crypto.randomUUID();
let busy = false;

const SUGGESTIONS = [
  "رصيد إجازاتي كام؟ ومين مديري؟",
  "إيه سياسة الإجازة المرضية؟",
  "لو أخدت إجازة من 4 لـ 8 أكتوبر، فيه إجازة رسمية في النص؟",
  "هات رصيد إجازات سارة",
  "اكتبلي قصيدة عن البحر",
];

// ---------- Startup ----------

init();

async function init() {
  for (const text of SUGGESTIONS) {
    const chip = document.createElement("button");
    chip.type = "button";
    chip.className = "chip";
    chip.textContent = text;
    chip.addEventListener("click", () => send(text));
    $("suggestions").appendChild(chip);
  }

  try {
    const info = await (await fetch("/api/info")).json();
    $("model").textContent = `${info.provider} · ${info.model}`;
    $("who").textContent = `مسجّل الدخول: ${info.employeeName}`;
  } catch {
    $("model").textContent = "offline";
  }
}

// ---------- Chat ----------

$("chatForm").addEventListener("submit", (e) => {
  e.preventDefault();
  send(inputEl.value);
});

inputEl.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    send(inputEl.value);
  }
});

inputEl.addEventListener("input", () => {
  inputEl.style.height = "auto";
  inputEl.style.height = inputEl.scrollHeight + "px";
});

$("newChat").addEventListener("click", async () => {
  if (busy) return;
  await fetch(`/api/conversations/${conversationId}`, { method: "DELETE" }).catch(() => {});
  conversationId = crypto.randomUUID();
  messagesEl.querySelectorAll(".msg").forEach((m) => m.remove());
  $("empty").hidden = false;
});

async function send(text) {
  text = text.trim();
  if (!text || busy) return;

  setBusy(true);
  $("empty").hidden = true;
  inputEl.value = "";
  inputEl.style.height = "auto";

  addMessage("user").bubble.textContent = text;
  await streamReply(`/api/conversations/${conversationId}/messages`, { message: text });
}

/** Sends a request and streams the agent's reply (text, tools, sources, approvals) into a new message. */
async function streamReply(url, payload) {
  setBusy(true);
  const reply = addMessage("assistant");
  reply.bubble.classList.add("typing");

  let answer = "";
  let pausedForApproval = false;

  try {
    const res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });

    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      throw new Error(body.message || `HTTP ${res.status}`);
    }

    for await (const { event, data } of readServerSentEvents(res)) {
      switch (event) {
        case "tool":
          addToolChip(reply, data);
          break;
        case "approval":
          pausedForApproval = true;
          addApprovalCard(reply, data);
          break;
        case "text":
          answer += data.delta;
          reply.bubble.innerHTML = renderMarkdown(answer);
          break;
        case "sources":
          for (const s of data.sources) {
            const chip = document.createElement("span");
            chip.className = "tool";
            chip.textContent = `📄 ${s.citation} (${s.score})`;
            reply.tools.appendChild(chip);
          }
          break;
        case "usage":
          reply.usage.textContent =
            `in: ${data.inputTokens ?? 0} · out: ${data.outputTokens ?? 0}` +
            ` ‖ session: ${data.sessionTotalTokens ?? 0} ‖ history: ${data.historyCount} msgs`;
          break;
        case "error":
          showError(reply, data.message);
          break;
      }
      scrollToBottom();
    }
  } catch (err) {
    showError(reply, err.message);
  } finally {
    reply.bubble.classList.remove("typing");
    if (!answer && pausedForApproval) reply.bubble.textContent = "⏸️ محتاج موافقتك قبل التنفيذ:";
    else if (!answer && !reply.el.querySelector(".error-box")) reply.bubble.textContent = "—";
    setBusy(false);
    inputEl.focus();
  }
}

/** Parses a text/event-stream response body into { event, data } objects as they arrive. */
async function* readServerSentEvents(response) {
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    let boundary;
    while ((boundary = buffer.indexOf("\n\n")) !== -1) {
      const raw = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);

      let event = "message";
      const dataLines = [];
      for (const line of raw.split("\n")) {
        if (line.startsWith("event:")) event = line.slice(6).trim();
        else if (line.startsWith("data:")) dataLines.push(line.slice(5).trimStart());
      }
      if (dataLines.length) yield { event, data: JSON.parse(dataLines.join("\n")) };
    }
  }
}

function addMessage(role) {
  const el = document.createElement("div");
  el.className = `msg ${role}`;

  const tools = document.createElement("div");
  tools.className = "tools";
  const bubble = document.createElement("div");
  bubble.className = "bubble";
  bubble.dir = "auto";
  const usage = document.createElement("div");
  usage.className = "usage";

  if (role === "assistant") el.append(tools);
  el.append(bubble);
  if (role === "assistant") el.append(usage);

  messagesEl.appendChild(el);
  scrollToBottom();
  return { el, tools, bubble, usage };
}

function addToolChip(reply, call) {
  const chip = document.createElement("span");
  chip.className = "tool";
  const args = call.arguments && Object.keys(call.arguments).length ? JSON.stringify(call.arguments) : "";
  chip.textContent = `🔧 ${call.name}(${args})`;
  reply.tools.appendChild(chip);
}

const LEAVE_TYPES = { Annual: "سنوية", Sick: "مرضية", Unpaid: "بدون أجر", Other: "أخرى" };

/**
 * Human-in-the-loop card. Shows the EXACT arguments the agent will run with — not the model's
 * own description of them — so the employee approves what really happens.
 */
function addApprovalCard(reply, approval) {
  const card = document.createElement("div");
  card.className = "approval";

  const title = document.createElement("div");
  title.className = "approval-title";
  title.textContent = approval.toolName === "SubmitLeaveRequest" ? "📝 تقديم طلب إجازة" : `🔧 ${approval.toolName}`;
  card.appendChild(title);

  const args = approval.arguments ?? {};
  const rows = approval.toolName === "SubmitLeaveRequest"
    ? [
        ["النوع", LEAVE_TYPES[args.type] ?? args.type],
        ["من", args.startDate],
        ["إلى", args.endDate],
        ["عدد الأيام", daysBetween(args.startDate, args.endDate)], // computed by code, not by the model
        ["السبب", args.reason || "—"],
      ]
    : Object.entries(args).map(([k, v]) => [k, typeof v === "object" ? JSON.stringify(v) : v]);

  const table = document.createElement("table");
  for (const [label, value] of rows) {
    const tr = table.insertRow();
    const th = document.createElement("th");
    th.textContent = label;
    const td = tr.insertCell();
    td.dir = "auto";
    td.textContent = value ?? "—";
    tr.prepend(th);
  }
  card.appendChild(table);

  const actions = document.createElement("div");
  actions.className = "approval-actions";
  const approve = document.createElement("button");
  approve.className = "btn primary";
  approve.textContent = "✅ موافق";
  const reject = document.createElement("button");
  reject.className = "btn ghost";
  reject.textContent = "❌ رفض";
  actions.append(approve, reject);
  card.appendChild(actions);

  const decide = async (approved) => {
    if (busy) return;
    approve.disabled = reject.disabled = true;
    actions.textContent = approved ? "✅ وافقت" : "❌ رفضت";
    await streamReply(`/api/conversations/${conversationId}/approvals/${encodeURIComponent(approval.requestId)}`, { approved });
  };
  approve.addEventListener("click", () => decide(true));
  reject.addEventListener("click", () => decide(false));

  reply.el.appendChild(card);
}

function daysBetween(start, end) {
  const s = Date.parse(start), e = Date.parse(end);
  if (Number.isNaN(s) || Number.isNaN(e)) return "?";
  return Math.round((e - s) / 86_400_000) + 1;
}

function showError(reply, message) {
  const box = document.createElement("div");
  box.className = "error-box";
  box.dir = "auto";
  box.textContent = `❌ ${message}`;
  reply.el.appendChild(box);
}

function setBusy(value) {
  busy = value;
  sendBtn.disabled = value;
  $("newChat").disabled = value;
}

function scrollToBottom() {
  messagesEl.scrollTop = messagesEl.scrollHeight;
}

/**
 * Minimal Markdown → HTML. Escapes EVERYTHING first: model output is untrusted input,
 * and inserting it as raw HTML would allow script injection (XSS).
 */
function renderMarkdown(text) {
  const escaped = text.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

  const inline = (s) => s
    .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
    .replace(/`([^`]+)`/g, "<code>$1</code>");

  const html = [];
  let list = null;

  for (const line of escaped.split("\n")) {
    const bullet = line.match(/^\s*[-*]\s+(.*)$/);
    const numbered = line.match(/^\s*\d+[.)]\s+(.*)$/);
    const item = bullet || numbered;
    const type = bullet ? "ul" : numbered ? "ol" : null;

    if (item) {
      if (list !== type) {
        if (list) html.push(`</${list}>`);
        html.push(`<${type}>`);
        list = type;
      }
      html.push(`<li>${inline(item[1])}</li>`);
      continue;
    }
    if (list) { html.push(`</${list}>`); list = null; }
    if (line.trim()) html.push(`<p>${inline(line)}</p>`);
  }
  if (list) html.push(`</${list}>`);
  return html.join("");
}

// ---------- Leave request (Structured Output) ----------

$("leaveForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const text = $("leaveText").value.trim();
  if (!text) return;

  const btn = $("leaveBtn");
  const out = $("leaveResult");
  btn.disabled = true;
  btn.textContent = "جاري الاستخراج…";
  out.hidden = false;
  out.textContent = "";

  try {
    const res = await fetch("/api/leave-requests/extract", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text }),
    });
    const body = await res.json();
    if (!res.ok) throw new Error(body.message || body.title || `HTTP ${res.status}`);
    renderLeave(out, body);
  } catch (err) {
    out.innerHTML = "";
    const box = document.createElement("div");
    box.className = "error-box";
    box.textContent = `❌ ${err.message}`;
    out.appendChild(box);
  } finally {
    btn.disabled = false;
    btn.textContent = "استخرج الطلب";
  }
});

function renderLeave(out, { draft, errors, usage }) {
  out.innerHTML = "";

  const rows = [
    ["النوع", draft?.type],
    ["من", draft?.startDate],
    ["إلى", draft?.endDate],
    ["عدد الأيام", draft?.days],
    ["السبب", draft?.reason],
  ];

  const table = document.createElement("table");
  for (const [label, value] of rows) {
    const tr = table.insertRow();
    const th = document.createElement("th");
    th.textContent = label;
    const td = tr.insertCell();
    td.dir = "auto";
    if (value === null || value === undefined) {
      td.textContent = "null";
      td.className = "null";
    } else {
      td.textContent = value;
    }
    tr.prepend(th);
  }
  out.appendChild(table);

  const verdict = document.createElement("div");
  verdict.className = "verdict";
  if (!errors.length) {
    const ok = document.createElement("div");
    ok.className = "ok";
    ok.textContent = "✅ الطلب سليم وجاهز للإرسال.";
    verdict.appendChild(ok);
  }
  for (const msg of errors) {
    const w = document.createElement("div");
    w.className = "warn";
    w.textContent = `⚠️ ${msg}`;
    verdict.appendChild(w);
  }
  out.appendChild(verdict);

  const u = document.createElement("div");
  u.className = "usage";
  u.style.marginTop = "8px";
  u.textContent = `in: ${usage.inputTokens ?? 0} · out: ${usage.outputTokens ?? 0}`;
  out.appendChild(u);
}

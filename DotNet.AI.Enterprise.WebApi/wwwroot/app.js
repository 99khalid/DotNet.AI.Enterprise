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
  "عندي كام يوم عطلة؟",
  "إيه سياسة الإجازة المرضية؟",
  "قدّملي إجازة سنوية من 2026-12-13 لـ 2026-12-14",
  "هات رصيد إجازات سارة",
  "اكتبلي قصيدة عن البحر",
];

// Which specialist the router picked — shown as a chip on every answer.
const ROUTE_LABELS = { Policy: "📚 سياسات", Profile: "👤 بياناتي", LeaveRequest: "📝 طلب إجازة", Other: "💬 خارج الـ HR" };

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
  let isLeaveRequest = false;
  let steps = null; // workflow steps list, created when a leave request is routed to the workflow

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
        case "route": {
          if (data.intent === "LeaveRequest") isLeaveRequest = true;
          // Several requests in one message: keep each specialist's answer in its own paragraph.
          if (data.part && answer) answer += "\n\n";
          const chip = document.createElement("span");
          chip.className = "tool route";
          chip.textContent = `🔀 ${ROUTE_LABELS[data.intent] ?? data.intent} · ${data.method} ${data.score}` +
            (data.part ? ` ← «${data.part}»` : "");
          reply.tools.appendChild(chip);
          break;
        }
        case "tool":
          addToolChip(reply, data);
          break;
        // Leave requests are routed to the workflow: same steps and card as the leave panel.
        case "step": {
          if (!steps) {
            steps = document.createElement("ol");
            steps.className = "wf-steps";
            reply.el.appendChild(steps);
          }
          const li = document.createElement("li");
          li.textContent = STEP_LABELS[data.step] ?? data.step;
          steps.appendChild(li);
          break;
        }
        case "proposal":
          addProposalCard(reply.el, steps, data);
          break;
        case "result":
          addLeaveResult(reply.el, data);
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
    if (!answer && isLeaveRequest) reply.bubble.textContent = "📝 طلب إجازة — اتنفّذ كـ Workflow:";
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

// ---------- Leave request (Workflow) ----------
// The panel runs the leave WORKFLOW: the code fixes the steps, the model only extracts the data.

const STEP_LABELS = {
  Extract: "🤖 استخراج البيانات من الكلام",
  Validate: "✔️ مراجعة قواعد الإجازات",
  NeedsInfo: "⚠️ الطلب محتاج تعديل",
  Enrich: "📊 الرصيد والإجازات الرسمية والسياسة",
  Submit: "📨 تقديم الطلب",
};

$("leaveForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const text = $("leaveText").value.trim();
  if (!text) return;

  const out = $("leaveResult");
  out.hidden = false;
  out.innerHTML = "";
  const steps = document.createElement("ol");
  steps.className = "wf-steps";
  out.appendChild(steps);

  await runLeaveWorkflow("/api/leave-requests/workflow/", { text }, out, steps, $("leaveBtn"));
});

/**
 * Streams workflow events: each finished step, then a proposal to approve or a final result.
 * Used by the leave panel and by approval cards shown in the chat (btn is the panel's button, if any).
 */
async function runLeaveWorkflow(url, payload, out, steps, btn) {
  if (btn) {
    btn.disabled = true;
    btn.textContent = "جاري التنفيذ…";
  }

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
        case "step": {
          const li = document.createElement("li");
          li.textContent = STEP_LABELS[data.step] ?? data.step;
          steps.appendChild(li);
          break;
        }
        case "proposal":
          addProposalCard(out, steps, data);
          break;
        case "result":
          addLeaveResult(out, data);
          break;
        case "error":
          addLeaveError(out, data.message);
          break;
      }
      scrollToBottom();
    }
  } catch (err) {
    addLeaveError(out, err.message);
  } finally {
    if (btn) {
      btn.disabled = false;
      btn.textContent = "قدّم الطلب";
    }
  }
}

/** Human-in-the-loop: everything shown here was computed by code from the extracted data. */
function addProposalCard(out, steps, { runId, proposal: p }) {
  const card = document.createElement("div");
  card.className = "approval";

  const title = document.createElement("div");
  title.className = "approval-title";
  title.textContent = "⏸️ راجع الطلب قبل التقديم";
  card.appendChild(title);

  const rows = [
    ["النوع", LEAVE_TYPES[p.type] ?? p.type],
    ["من", p.startDate],
    ["إلى", p.endDate],
    ["عدد الأيام", p.days],
    ["السبب", p.reason || "—"],
    ["الرصيد", p.balanceAfter === null ? `${p.balanceBefore}` : `${p.balanceBefore} ← ${p.balanceAfter}`],
    ["إجازات رسمية في الفترة", p.holidaysInRange.length ? p.holidaysInRange.join("، ") : "مفيش"],
    ["السياسة", p.policy ?? "—"],
  ];

  const table = document.createElement("table");
  for (const [label, value] of rows) {
    const tr = table.insertRow();
    const th = document.createElement("th");
    th.textContent = label;
    const td = tr.insertCell();
    td.dir = "auto";
    td.textContent = value;
    tr.prepend(th);
  }
  card.appendChild(table);

  const actions = document.createElement("div");
  actions.className = "approval-actions";
  const approve = document.createElement("button");
  approve.type = "button";
  approve.className = "btn primary";
  approve.textContent = "✅ قدّم الطلب";
  const reject = document.createElement("button");
  reject.type = "button";
  reject.className = "btn ghost";
  reject.textContent = "❌ إلغاء";
  actions.append(approve, reject);
  card.appendChild(actions);

  const decide = async (approved) => {
    approve.disabled = reject.disabled = true;
    actions.textContent = approved ? "✅ وافقت" : "❌ لغيت";
    await runLeaveWorkflow(`/api/leave-requests/workflow/${encodeURIComponent(runId)}/decision`, { approved }, out, steps);
  };
  approve.addEventListener("click", () => decide(true));
  reject.addEventListener("click", () => decide(false));

  out.appendChild(card);
}

function addLeaveResult(out, result) {
  const verdict = document.createElement("div");
  verdict.className = "verdict";

  const main = document.createElement("div");
  main.className = result.submitted ? "ok" : "warn";
  main.textContent = result.message;
  verdict.appendChild(main);

  for (const problem of result.problems) {
    const w = document.createElement("div");
    w.className = "warn";
    w.textContent = `• ${problem}`;
    verdict.appendChild(w);
  }
  out.appendChild(verdict);
}

function addLeaveError(out, message) {
  const box = document.createElement("div");
  box.className = "error-box";
  box.textContent = `❌ ${message}`;
  out.appendChild(box);
}

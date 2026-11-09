/* 自询 · 买前三问 · 前端（原生 JS，零依赖） */

/* 三问标题（决策视角，静态写死，不由 AI 返回） */
const QUESTION_TITLES = [
  '这门生意赚的是不是真钱？<span class="q-sub">（靠什么赚钱）</span>',
  '现在这个价，买贵了没有？<span class="q-sub">（贵不贵）</span>',
  '万一买错了，最坏亏多少？<span class="q-sub">（最坏会怎样）</span>'
];
const NUMS = ["①", "②", "③"];

const input = document.getElementById("stockInput");
const acBox = document.getElementById("ac");
let lastResult = null;   // 最近一次 analyze 响应（PDF 导出用）
let lastAnswers = ["", "", ""];
let acTimer = null;

/* ---------- 自动补全（GET /api/suggest，前缀匹配优先） ---------- */
input.addEventListener("input", function () {
  const q = this.value.trim();
  clearTimeout(acTimer);
  if (!q) { hideAc(); return; }
  acTimer = setTimeout(async () => {
    const m = document.getElementById("market").value;
    try {
      const resp = await fetch(`/api/suggest?q=${encodeURIComponent(q)}&market=${m}`);
      const hits = await resp.json();
      if (!hits.length) { hideAc(); return; }
      acBox.innerHTML = hits.map(s =>
        `<div class="ac-item" data-name="${escapeAttr(s.name)}" data-code="${s.code}">
           <span class="name">${escapeHtml(s.name)}</span><span class="code">${escapeHtml(s.code)}</span>
         </div>`
      ).join("");
      acBox.style.display = "block";
      acBox.querySelectorAll(".ac-item").forEach(el => {
        el.addEventListener("click", function () {
          input.value = this.dataset.name;
          hideAc();
        });
      });
    } catch { hideAc(); }
  }, 200);
});
input.addEventListener("blur", function () { setTimeout(hideAc, 150); });
function hideAc() { acBox.style.display = "none"; }

document.getElementById("market").addEventListener("change", function () {
  hideAc();
  input.value = "";
  hideResults();
  document.getElementById("questionForm").style.display = "none";
});

/* ---------- 开始 → 显示三问表单（不调 AI） ---------- */
function start() {
  const q = input.value.trim();
  if (!q) { input.focus(); return; }
  document.getElementById("badgeName").textContent = q;
  document.getElementById("badgeCode").textContent = "";
  hideResults();
  hideError();
  const form = document.getElementById("questionForm");
  form.style.display = "block";
  form.scrollIntoView({ behavior: "smooth", block: "start" });
}

/* ---------- 提交 → POST /api/analyze ---------- */
async function submitAnswers() {
  const query = input.value.trim();
  if (!query) { input.focus(); return; }
  lastAnswers = ["ans1", "ans2", "ans3"].map(id => document.getElementById(id).value.trim());

  const btn = document.getElementById("submitBtn");
  btn.disabled = true;
  document.getElementById("loading").style.display = "block";
  hideResults();
  hideError();

  try {
    const resp = await fetch("/api/analyze", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        query,
        market: document.getElementById("market").value,
        answers: lastAnswers
      })
    });
    const data = await resp.json();
    if (!resp.ok) { showError(data.error || "分析失败，请重试"); return; }
    lastResult = data;
    render(data);
  } catch {
    showError("网络错误，请确认服务已启动后重试");
  } finally {
    document.getElementById("loading").style.display = "none";
    btn.disabled = false;
  }
}

function hideResults() { document.getElementById("results").style.display = "none"; }
function showError(msg) {
  const el = document.getElementById("error");
  el.textContent = msg;
  el.style.display = "block";
}
function hideError() { document.getElementById("error").style.display = "none"; }

/* ---------- 渲染三问回应卡片 ---------- */
function render(data) {
  document.getElementById("badgeName").textContent = (data.stock.displayName || data.stock.name);
  document.getElementById("badgeCode").textContent = data.stock.displayCode || data.stock.code;

  const results = document.getElementById("results");
  results.innerHTML = `
    <div class="stock-title">
      <div class="name">${escapeHtml((data.stock.displayName || data.stock.name))}</div>
      <div class="meta">${escapeHtml(data.stock.displayCode || data.stock.code)} · ${escapeHtml(data.disclaimer)}</div>
    </div>
    ${data.responses.map((r, i) => card(i, r, lastAnswers[i] || "")).join("")}
  `;
  results.style.display = "block";
  document.getElementById("pdfBtn").style.display = "inline-block";
  results.scrollIntoView({ behavior: "smooth", block: "start" });
}

function card(i, r, userAnswer) {
  return `
    <div class="q-card" style="animation-delay:${(i + 1) * 0.12}s">
      <h3><span class="num">${NUMS[i]}</span>${QUESTION_TITLES[i]}</h3>
      ${userAnswer ? `<div class="user-answer">你的回答：${escapeHtml(userAnswer)}</div>` : ""}
      <div class="answer">${renderAnswer(r)}</div>
    </div>`;
}

/* [term] → 可点击展开的内联 span；无标记时兜底平铺术语表 */
function renderAnswer(r) {
  let html = escapeHtml(r.response);
  // 模型输出里的 **加粗** 标记转成真正的加粗
  html = html.replace(/\*\*([^*]+)\*\*/g, "<b>$1</b>");
  let replaced = 0;
  for (const t of r.terms || []) {
    const marked = escapeHtml("[" + t.term + "]");
    if (html.includes(marked)) {
      replaced++;
      html = html.split(marked).join(
        `<span class="term" onclick="toggleTerm(this)">${escapeHtml(t.term)}<span class="tip">（${escapeHtml(t.plain)}）</span></span>`
      );
    }
  }
  // 模型没按约定打 [term] 标记时，术语表平铺在卡片底部，不丢信息
  if (replaced === 0 && (r.terms || []).length > 0) {
    html += `<div class="term-list">` + r.terms.map(t =>
      `<div class="term-row"><b>${escapeHtml(t.term)}</b>：${escapeHtml(t.plain)}</div>`
    ).join("") + `</div>`;
  }
  return html;
}

function toggleTerm(el) {
  const tip = el.querySelector(".tip");
  if (tip) tip.classList.toggle("show");
}

/* ---------- 导出 PDF（POST /api/export-pdf → 下载） ---------- */
async function exportPdf() {
  if (!lastResult) return;
  const btn = document.getElementById("pdfBtn");
  btn.disabled = true;
  try {
    const resp = await fetch("/api/export-pdf", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        stock: lastResult.stock,
        answers: lastAnswers,
        responses: lastResult.responses
      })
    });
    if (!resp.ok) { showError("PDF 生成失败，请重试"); return; }
    const blob = await resp.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `自询-${(lastResult.stock.displayName || lastResult.stock.name)}.pdf`;
    a.click();
    URL.revokeObjectURL(url);
  } catch {
    showError("PDF 导出失败，请重试");
  } finally {
    btn.disabled = false;
  }
}

function escapeHtml(s) {
  return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}
function escapeAttr(s) { return escapeHtml(s); }

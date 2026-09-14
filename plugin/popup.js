const keywordInput = document.querySelector('#keyword');
const extensionVersion = document.querySelector('#extensionVersion');
const startButton = document.querySelector('#start');
const status = document.querySelector('#status');
const resultSection = document.querySelector('#resultSection');
const resultOutput = document.querySelector('#result');
const copyButton = document.querySelector('#copy');

extensionVersion.textContent = `v${chrome.runtime.getManifest().version}`;

startButton.addEventListener('click', async () => {
  const keyword = keywordInput.value.trim();
  if (!keyword) {
    setStatus('请输入搜索关键词。', 'error');
    return;
  }

  startButton.disabled = true;
  setStatus('正在创建任务…', 'running');
  const response = await chrome.runtime.sendMessage({
    type: 'START_1688_DOM_DEMO',
    keyword,
    maxItems: 60,
  });

  if (!response?.accepted) {
    startButton.disabled = false;
    setStatus(response?.error || '任务创建失败。', 'error');
    return;
  }
  setStatus('任务已启动；弹窗关闭后可通过扩展徽标查看结果。', 'running');
});

copyButton.addEventListener('click', async () => {
  await navigator.clipboard.writeText(resultOutput.textContent);
  copyButton.textContent = '已复制';
});

await refreshState();

async function refreshState() {
  const state = await chrome.runtime.sendMessage({ type: 'GET_DEMO_STATE' });
  if (!state || state.status === 'idle') return;

  keywordInput.value = state.keyword || keywordInput.value;
  startButton.disabled = state.status === 'running';
  setStatus(state.step || state.status, state.status);

  if (state.result) {
    resultOutput.textContent = JSON.stringify(state.result, null, 2);
    resultSection.hidden = false;
  }
}

function setStatus(message, state) {
  status.textContent = message;
  status.dataset.state = state;
}

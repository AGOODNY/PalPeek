(() => {
  'use strict';
  const inviteId = location.pathname.split('/').filter(Boolean)[1] || '';
  const $ = id => document.getElementById(id);
  const loginPanel = $('loginPanel');
  const watchPanel = $('watchPanel');
  const loginForm = $('loginForm');
  const loginButton = $('loginButton');
  const watchButton = $('watchButton');
  const player = $('player');
  const overlay = $('videoOverlay');
  let status = null;
  let leaseId = null;
  let heartbeatTimer = null;
  let statusTimer = null;
  let hls = null;

  $('viewerName').value = localStorage.getItem('palpeek-viewer-name') || '';

  function setBadge(text, online = false) {
    $('connectionBadge').textContent = text;
    $('connectionBadge').classList.toggle('online', online);
  }

  function showOverlay(title, detail, canStart = false) {
    overlay.hidden = false;
    $('overlayTitle').textContent = title;
    $('overlayDetail').textContent = detail || '';
    watchButton.hidden = !canStart;
  }

  function showLogin(message = '') {
    stopPlayback(false);
    loginPanel.hidden = false;
    watchPanel.hidden = true;
    $('loginError').textContent = message;
    setBadge('需要验证');
  }

  async function jsonFetch(url, options = {}) {
    const response = await fetch(url, {
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
      ...options
    });
    let body = null;
    try { body = await response.json(); } catch { }
    if (!response.ok) {
      const error = new Error(body?.message || `请求失败（${response.status}）`);
      error.status = response.status;
      throw error;
    }
    return body;
  }

  loginForm.addEventListener('submit', async event => {
    event.preventDefault();
    if (!inviteId) {
      $('loginError').textContent = '观战链接无效。';
      return;
    }
    loginButton.disabled = true;
    $('loginError').textContent = '';
    const viewerName = $('viewerName').value.trim();
    try {
      await jsonFetch(`/api/web/v1/auth/${encodeURIComponent(inviteId)}`, {
        method: 'POST',
        body: JSON.stringify({ inviteId, password: $('password').value, viewerName })
      });
      localStorage.setItem('palpeek-viewer-name', viewerName);
      $('password').value = '';
      loginPanel.hidden = true;
      watchPanel.hidden = false;
      await refreshStatus();
      startStatusPolling();
    } catch (error) {
      $('loginError').textContent = error.status === 429 ? '尝试次数过多，请稍后再试。' : error.message;
    } finally {
      loginButton.disabled = false;
    }
  });

  async function refreshStatus() {
    try {
      status = await jsonFetch('/api/web/v1/status');
      setBadge(status.online ? '已连接主播' : '主播离线', status.online);
      $('hostName').textContent = status.host ? `主播：${status.host}` : '主播暂时离线';
      $('gameName').textContent = status.game?.name || '尚未开播';
      $('quality').textContent = status.quality === 'P720_60' ? '720P · 60FPS' : '720P · 30FPS';
      $('viewerCount').textContent = `${status.viewerCount ?? 0}/${status.maxViewers ?? 3}`;
      if (!leaseId) {
        if (status.canWatch) showOverlay('直播已就绪', '点击后开始有声播放。', true);
        else if (status.game) showOverlay('视频正在准备', status.message || '请稍候…');
        else showOverlay('等待主播开播', status.message || '页面会自动检查直播状态。');
      } else if (!status.game) {
        stopPlayback(false);
        showOverlay('分享已结束', '主播已经停止本次游戏分享。');
      }
    } catch (error) {
      if (error.status === 401) showLogin('登录已失效，请重新输入口令。');
      else setBadge('连接中断');
    }
  }

  function startStatusPolling() {
    clearInterval(statusTimer);
    statusTimer = setInterval(refreshStatus, 3000);
  }

  watchButton.addEventListener('click', async () => {
    if (!status?.game) return;
    watchButton.disabled = true;
    $('watchError').textContent = '';
    showOverlay('正在连接视频', '首次启动通常需要几秒钟。');
    try {
      const lease = await jsonFetch('/api/web/v1/viewers', {
        method: 'POST',
        body: JSON.stringify({ sessionId: status.game.sessionId })
      });
      leaseId = lease.leaseId;
      startHeartbeat();
      attachPlayer(lease.playlist);
    } catch (error) {
      leaseId = null;
      $('watchError').textContent = error.message;
      showOverlay('暂时无法观战', error.message, status?.canWatch);
    } finally {
      watchButton.disabled = false;
    }
  });

  function attachPlayer(url) {
    if (player.canPlayType('application/vnd.apple.mpegurl')) {
      player.src = url;
      player.play().then(() => { overlay.hidden = true; }).catch(showPlayError);
      return;
    }
    if (window.Hls?.isSupported()) {
      hls = new window.Hls({
        liveSyncDurationCount: 3,
        liveMaxLatencyDurationCount: 7,
        manifestLoadingMaxRetry: 12,
        manifestLoadingRetryDelay: 750
      });
      hls.loadSource(url);
      hls.attachMedia(player);
      hls.on(window.Hls.Events.MANIFEST_PARSED, () =>
        player.play().then(() => { overlay.hidden = true; }).catch(showPlayError));
      hls.on(window.Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return;
        if (data.type === window.Hls.ErrorTypes.NETWORK_ERROR) {
          showOverlay('正在重新连接', '网络暂时中断，PalPeek 正在重试。');
          hls.startLoad();
        } else if (data.type === window.Hls.ErrorTypes.MEDIA_ERROR) {
          showOverlay('正在恢复播放', '浏览器正在重新初始化解码器。');
          hls.recoverMediaError();
        } else {
          showPlayError(new Error('视频连接中断，请重新开始观战。'));
        }
      });
      return;
    }
    showPlayError(new Error('当前浏览器不支持 HLS 播放，请升级到最新版浏览器。'));
  }

  function showPlayError(error) {
    $('watchError').textContent = error.message;
    showOverlay('播放未能启动', error.message, true);
  }

  function startHeartbeat() {
    clearInterval(heartbeatTimer);
    heartbeatTimer = setInterval(async () => {
      if (!leaseId) return;
      try {
        await jsonFetch(`/api/web/v1/viewers/${encodeURIComponent(leaseId)}/heartbeat`, {
          method: 'PUT', body: '{}'
        });
      } catch {
        stopPlayback(false);
        showOverlay('连接已经过期', '请重新点击开始观战。', Boolean(status?.canWatch));
      }
    }, 5000);
  }

  function stopPlayback(release = true) {
    const oldLease = leaseId;
    leaseId = null;
    clearInterval(heartbeatTimer);
    if (hls) { hls.destroy(); hls = null; }
    player.pause();
    player.removeAttribute('src');
    player.load();
    if (release && oldLease) {
      fetch(`/api/web/v1/viewers/${encodeURIComponent(oldLease)}`, {
        method: 'DELETE', credentials: 'same-origin', keepalive: true
      }).catch(() => {});
    }
  }

  window.addEventListener('pagehide', () => stopPlayback(true));
  player.addEventListener('playing', () => { overlay.hidden = true; });
  player.addEventListener('error', () => {
    if (leaseId && !hls) showPlayError(new Error('视频连接中断，请重新开始观战。'));
  });

  (async () => {
    if (!inviteId) {
      showLogin('观战链接无效，请检查地址。');
      return;
    }
    try {
      const auth = await jsonFetch('/api/web/v1/auth');
      if (!auth.authenticated) {
        showLogin();
        return;
      }
      loginPanel.hidden = true;
      watchPanel.hidden = false;
      await refreshStatus();
      if (!loginPanel.hidden) return;
      startStatusPolling();
    } catch {
      showLogin();
    }
  })();
})();

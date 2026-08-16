// 与后端 /api 交互的封装。所有接口均为同源（由 Kestrel 托管）。
// 登录鉴权：token 存 sessionStorage（勾选记住则同时存 localStorage），
// 请求统一带 Authorization: Bearer <token>；SSE/下载等无法带头的场景用 ?token= 查询参数。

const TOKEN_KEY = 'hxsfm_auth_token'

export function getToken() {
  return sessionStorage.getItem(TOKEN_KEY) || localStorage.getItem(TOKEN_KEY) || ''
}

export function setToken(token, remember) {
  sessionStorage.setItem(TOKEN_KEY, token)
  if (remember) localStorage.setItem(TOKEN_KEY, token)
  else localStorage.removeItem(TOKEN_KEY)
}

export function clearToken() {
  sessionStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(TOKEN_KEY)
}

// 401 时回调（App 切回登录页）；登录接口自身的 401 不触发
let unauthorizedHandler = null
export function setUnauthorizedHandler(fn) {
  unauthorizedHandler = fn
}

function onUnauthorized() {
  clearToken()
  if (unauthorizedHandler) unauthorizedHandler()
}

// SSH.NET 底层异常（会话被空闲回收/远端断开后抛 "Client not connected." 之类）统一成友好文案，
// 避免各界面把英文堆栈直接透传给用户。出现该文案 = 连接已死，App 会通过会话健康横幅提示重连。
function normalizeError(msg) {
  return /client not connected|not connected|connection (is )?closed/i.test(msg)
    ? '连接已断开，请重新连接'
    : msg
}

async function request(url, options = {}) {
  const token = getToken()
  const headers = { ...(options.headers || {}) }
  // 非 FormData 的 body 才需要 JSON 头（上传走 FormData，浏览器自动带 boundary）
  if (options.body && !(options.body instanceof FormData)) {
    headers['Content-Type'] = 'application/json'
  }
  if (token) headers['Authorization'] = `Bearer ${token}`
  const res = await fetch(url, { ...options, headers })
  if (res.status === 401 && !url.includes('/api/auth/login')) {
    onUnauthorized()
    throw new Error('登录已过期，请重新登录')
  }
  if (!res.ok) {
    let msg = `请求失败 (${res.status})`
    try {
      const body = await res.json()
      if (body && body.error) msg = body.error
    } catch (_) { /* ignore */ }
    throw new Error(normalizeError(msg))
  }
  // 204 / 无内容
  const ct = res.headers.get('content-type') || ''
  if (ct.includes('application/json')) return await res.json()
  return null
}

// 登录接口单独处理：401 也要拿到完整响应体（error/locked/remainingAttempts）展示给用户
async function loginRequest(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const err = new Error(data.error || `登录失败 (${res.status})`)
    err.locked = data.locked
    err.remainingAttempts = data.remainingAttempts
    throw err
  }
  return data
}

export const api = {
  health: () => request('/api/health'),

  // ---- 登录鉴权 ----
  session: () => request('/api/session'),
  login: (key, remember) => loginRequest('/api/auth/login', { key, remember }),
  logout: () => request('/api/auth/logout', { method: 'POST' }),

  connect: (req) =>
    request('/api/connect', { method: 'POST', body: JSON.stringify(req) }),

  disconnect: (connId) =>
    request('/api/disconnect', { method: 'POST', body: JSON.stringify({ connectionId: connId }) }),

  listConnections: () => request('/api/connections'),

  // 活跃 SSH 会话健康检查：返回 [{ connectionId, connected }]
  sessionsHealth: () => request('/api/connections/health'),

  reconnect: (id) =>
    request('/api/connections/reconnect', { method: 'POST', body: JSON.stringify({ connectionId: id }) }),

  updateConnection: (id, req) =>
    request(`/api/connections/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: JSON.stringify(req),
    }),

  deleteConnection: (id) =>
    request(`/api/connections/${encodeURIComponent(id)}`, { method: 'DELETE' }),

  // 导出/导入连接（导出为明文 JSON，含密码/私钥，用于备份迁移）
  exportConnections: () => request('/api/connections/export'),
  // mode: 'merge'（去重合并，host|port|username|password 一致才判重）| 'replace'（覆盖导入）
  importConnections: (profiles, mode = 'merge') =>
    request(`/api/connections/import?mode=${encodeURIComponent(mode)}`, {
      method: 'POST',
      body: JSON.stringify(profiles),
    }),

  listFiles: (connId, path) =>
    request(`/api/files?connId=${encodeURIComponent(connId)}&path=${encodeURIComponent(path)}`),

  mkdir: (connId, path, name) =>
    request('/api/mkdir', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId, path, name }),
    }),

  rename: (connId, dir, oldName, newPath) =>
    request('/api/rename', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId, path: dir, name: oldName, newPath }),
    }),

  remove: (connId, dir, name) =>
    request('/api/delete', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId, path: dir, name }),
    }),

  // 下载：<a download> 不能带请求头，token 放查询参数（后端会转成 Authorization 头）
  downloadUrl: (connId, path) =>
    `/api/download?connId=${encodeURIComponent(connId)}&path=${encodeURIComponent(path)}&token=${encodeURIComponent(getToken())}`,

  // 上传：用 XMLHttpRequest 以支持上传进度回调（fetch 无 upload 进度事件）。
  // onProgress(percent 0-100) 可选；signal 传 AbortController.signal 可取消（xhr.abort）。
  // 401/错误语义与 request() 保持一致。
  uploadFile: (connId, path, file, onProgress, signal) =>
    new Promise((resolve, reject) => {
      const form = new FormData()
      form.append('connId', connId)
      form.append('path', path)
      form.append('file', file)
      const xhr = new XMLHttpRequest()
      xhr.open('POST', '/api/upload')
      const token = getToken()
      if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`)
      if (signal) {
        if (signal.aborted) {
          reject(new Error('上传已取消'))
          return
        }
        signal.addEventListener('abort', () => xhr.abort(), { once: true })
      }
      if (onProgress) {
        xhr.upload.onprogress = (e) => {
          if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100))
        }
      }
      xhr.onabort = () => reject(new Error('上传已取消'))
      xhr.onload = () => {
        if (xhr.status === 401 && token) {
          onUnauthorized()
          reject(new Error('登录已过期，请重新登录'))
          return
        }
        if (xhr.status < 200 || xhr.status >= 300) {
          let msg = `上传失败 (${xhr.status})`
          try { const b = JSON.parse(xhr.responseText); if (b && b.error) msg = b.error } catch (_) {}
          reject(new Error(normalizeError(msg)))
          return
        }
        try { resolve(JSON.parse(xhr.responseText)) } catch (_) { resolve(null) }
      }
      xhr.onerror = () => reject(new Error(normalizeError('网络错误，上传失败')))
      xhr.send(form)
    }),

  // 读取文本文件内容（在线编辑）。后端已改为原始字节流返回（不经 JSON，避免非 ASCII 转义膨胀），
  // 这里用 fetch 流式读取；onProgress({ loaded, total, percent, chunk }) 可选回调，
  // chunk 为每块的文本增量，编辑器据此边收边显示（cat 式渐进体验）。
  getFileContent: (connId, path, onProgress) =>
    fetch(
      `/api/file-content?connId=${encodeURIComponent(connId)}&path=${encodeURIComponent(path)}`,
      { headers: getToken() ? { Authorization: `Bearer ${getToken()}` } : {} }
    )
      .then(async (res) => {
        if (res.status === 401 && getToken()) {
          onUnauthorized()
          throw new Error('登录已过期，请重新登录')
        }
        if (!res.ok) {
          let msg = `请求失败 (${res.status})`
          try { const b = await res.json(); if (b && b.error) msg = b.error } catch (_) {}
          throw new Error(normalizeError(msg))
        }
        const total = Number(res.headers.get('content-length')) || 0
        const reader = res.body?.getReader()
        const decoder = new TextDecoder()
        const parts = []
        let loaded = 0
        try {
          if (reader) {
            for (;;) {
              const { done, value } = await reader.read()
              if (done) break
              loaded += value.byteLength
              const chunk = decoder.decode(value, { stream: true })
              parts.push(chunk)
              if (onProgress) {
                onProgress({ loaded, total, percent: total ? Math.round((loaded / total) * 100) : 0, chunk })
              }
            }
          }
        } catch (e) {
          // 中途断开（远端 SSH 掉线等）：正常报错，已读部分不保证完整
          throw new Error(normalizeError(`读取中断：${(e && e.message) || e}`))
        }
        parts.push(decoder.decode())
        return { content: parts.join(''), size: loaded }
      })
      .catch((e) => {
        // 网络层失败（fetch 本身抛错）统一转友好文案
        throw new Error(normalizeError((e && e.message) || e))
      }),

  saveFileContent: (connId, path, content) =>
    request('/api/file-content', {
      method: 'PUT',
      body: JSON.stringify({ connectionId: connId, path, content }),
    }),

  runCommand: (connId, command) =>
    request('/api/command', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId, command }),
    }),

  setCwd: (connId, path) =>
    request('/api/cwd', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId, path }),
    }),

  // 交互终端（SSH shell + pty）
  terminalOpen: (connId, cols, rows) =>
    request('/api/terminal/open', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId, cols, rows }),
    }),

  terminalClose: (connId) =>
    request('/api/terminal/close', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId }),
    }),

  // WebSocket 双向通道（输入+输出共一条连接，token 走 ?token= 查询参数）
  terminalWsUrl: (connId) => {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:'
    return `${proto}//${location.host}/api/terminal/ws?connId=${encodeURIComponent(connId)}&token=${encodeURIComponent(getToken())}`
  },

  // ---- 用户偏好设置：常用目录收藏 + 终端宏（Data/settings.json）----
  getFavorites: () => request('/api/settings/favorites'),
  putFavorites: (favorites) =>
    request('/api/settings/favorites', { method: 'PUT', body: JSON.stringify(favorites) }),

  getMacros: () => request('/api/settings/macros'),
  putMacros: (macros) =>
    request('/api/settings/macros', { method: 'PUT', body: JSON.stringify(macros) }),

  // 当前连接对应服务器的系统状态（一次 SSH 采集）
  systemStatus: (connId) =>
    request(`/api/system-status?connId=${encodeURIComponent(connId)}`),
}

// SSE 实时日志流（EventSource 不能带请求头，token 走查询参数）
export function openLogStream(onMessage) {
  const token = getToken()
  const url = `/api/logs/stream${token ? `?token=${encodeURIComponent(token)}` : ''}`
  const es = new EventSource(url)
  es.onmessage = (e) => {
    try {
      const entry = JSON.parse(e.data)
      onMessage(entry)
    } catch (_) { /* ignore */ }
  }
  es.onerror = () => { /* EventSource 会自动重连 */ }
  return es
}

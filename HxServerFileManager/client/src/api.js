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

  // 仅保存连接（不发起 SSH 连接）：对同 host|port|username 的已有连接，凭据留空则保留原值
  saveConnection: (req) =>
    request('/api/connections', { method: 'POST', body: JSON.stringify(req) }),

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

  // 批量创建远端目录（上传文件夹用：父目录/空目录一并建，已存在则跳过）
  ensureDirs: (connId, path, dirs) =>
    request('/api/ensure-dirs', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId, path, dirs }),
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
  // 批量下载（多选文件/文件夹）：桌面壳 POST 此地址，远端 tar 流解包到本地文件夹
  downloadManyUrl: (connId) =>
    `/api/download-many?connId=${encodeURIComponent(connId)}&token=${encodeURIComponent(getToken())}`,

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

  // 服务器间直传（不经本机中转）：在源服务器上 scp 到目标服务器，返回 { jobId, total }
  serverCopy: (payload) =>
    request('/api/server-copy', {
      method: 'POST',
      body: JSON.stringify(payload),
    }),

  // 直传进度：{ id, total, done, state, error, source, target, targetDir, items }
  serverCopyStatus: (jobId) => request(`/api/server-copy/${encodeURIComponent(jobId)}`),

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

  // ---- 全局代理：连接级「跟随全局」的 SSH 连接使用（{type, host, port, username, password} | null）----
  getProxy: () => request('/api/settings/proxy'),
  putProxy: (proxy) =>
    request('/api/settings/proxy', { method: 'PUT', body: JSON.stringify(proxy ?? null) }),

  // ---- 命令历史：Terminal 执行过的命令（双击再次执行）----
  getHistory: () => request('/api/settings/history'),
  addHistory: (item) =>
    request('/api/settings/history', { method: 'POST', body: JSON.stringify(item) }),
  clearHistory: (connKey) =>
    request(`/api/settings/history?connKey=${encodeURIComponent(connKey || '')}`, { method: 'DELETE' }),

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

// SSE 实时网络上下行（后端常驻一条 SSH 通道每 intervalSec 秒采一次 /proc/net/dev 并算速率）
export function openNetStream(connId, onMessage, intervalSec = 1) {
  const token = getToken()
  const qs = new URLSearchParams({ connId, interval: String(intervalSec) })
  if (token) qs.set('token', token)
  const es = new EventSource(`/api/net-stream?${qs.toString()}`)
  es.onmessage = (e) => {
    try {
      onMessage(JSON.parse(e.data))
    } catch (_) { /* ignore */ }
  }
  es.onerror = () => { /* 会话断开等：EventSource 自己重连 */ }
  return es
}

// SSE 按进程的实时上下行（后端按会话探测 ss / nethogs，逐 socket 做差归到 pid）。
// 只在进程带宽面板打开时才建流；默认 2s 采样（ss 在海量 socket 机器上不便宜）。
export function openProcNetStream(connId, onMessage, intervalSec = 2) {
  const token = getToken()
  const qs = new URLSearchParams({ connId, interval: String(intervalSec) })
  if (token) qs.set('token', token)
  const es = new EventSource(`/api/proc-net-stream?${qs.toString()}`)
  es.onmessage = (e) => {
    try {
      onMessage(JSON.parse(e.data))
    } catch (_) { /* ignore */ }
  }
  es.onerror = () => { /* 会话断开等：EventSource 自己重连 */ }
  return es
}
// JS→C#：window.external.sendMessage(JSON)；C#→JS：window.external.receiveMessage = fn（Photino 注入）。
// 仅存在于桌面壳（WebView2/WebKitGTK）；普通浏览器里 window.external 没有 sendMessage，静默跳过。
// 协议：请求 { op, ... } → C# 处理 → 响应 { op: '<op>Result', ... }，按 op 分发一次性回调；
// 持续事件（desktopDragState / desktopDrop，Linux 拖拽）走 onDesktopEvent 订阅。
const desktopOps = {}
// C# → JS 的持续事件订阅：onDesktopEvent(op, fn) 返回取消订阅函数。
// 一次性结果仍走 desktopOps（任务完成即删），这里只处理没有一次性 handler 的事件。
const desktopEvents = {}
export function onDesktopEvent(op, fn) {
  if (!desktopEvents[op]) desktopEvents[op] = new Set()
  desktopEvents[op].add(fn)
  return () => desktopEvents[op]?.delete(fn)
}
if (typeof window !== 'undefined' && window.external?.sendMessage) {
  // Photino 注入的 receiveMessage 是「传入回调注册」的函数（内部监听 chrome.webview 的 message 事件），
  // 必须调用它注册回调（如 window.external.receiveMessage(cb)），不能赋值覆盖，否则 C#→JS 消息收不到
  window.external.receiveMessage((raw) => {
    let msg = null
    try { msg = JSON.parse(raw) } catch (_) { return }
    const h = desktopOps[msg?.op]
    if (h) {
      delete desktopOps[msg.op]
      h(msg)
      return
    }
    const ev = desktopEvents[msg?.op]
    if (ev) ev.forEach((fn) => { try { fn(msg) } catch (_) { /* 单个监听器异常不影响其他 */ } })
  })
}

let desktopMode = null
// 是否运行在桌面壳里（/api/health 的 desktop 标记，由桌面壳进程设 HXSFM_DESKTOP=1 产生）
export async function isDesktop() {
  if (desktopMode === null) {
    try {
      const h = await api.health()
      desktopMode = !!h.desktop
    } catch (_) {
      desktopMode = false
    }
  }
  return desktopMode
}

// 桌面壳：弹原生「另存为」对话框让用户选保存路径，把内容写入所选位置。
// 返回写入路径；用户取消返回 null；非桌面环境或出错 reject。浏览器端请走 <a download>。
export function desktopSaveTextFile(defaultName, content, timeoutMs = 60000) {
  return new Promise((resolve, reject) => {
    if (typeof window === 'undefined' || !window.external?.sendMessage) {
      reject(new Error('当前不是桌面环境，无法选择保存路径'))
      return
    }
    const timer = setTimeout(() => {
      delete desktopOps.saveFileResult
      reject(new Error('保存对话框无响应'))
    }, timeoutMs)
    desktopOps.saveFileResult = (msg) => {
      clearTimeout(timer)
      if (msg.ok) resolve(msg.path)
      else if (msg.canceled) resolve(null)
      else reject(new Error(msg.error || '保存失败'))
    }
    window.external.sendMessage(JSON.stringify({ op: 'saveFile', defaultName, content }))
  })
}

let downloadManySeq = 0
// 桌面壳：弹原生文件夹选择器选一个本地目录，远端 tar 流解包到该目录（保留目录结构）。
// 返回 { promise, cancel }：promise 解析为 { path, count }（用户取消/停止返回 null）或 reject；
// cancel() 发取消消息终止本次下载（对应「停止下载」按钮）。非桌面环境 reject。
// onProgress(file) 回传当前解包的文件名（节流后）。不设超时，等 C# 回传结果。
export function desktopDownloadMany(url, paths, onProgress) {
  let cancelFn = null
  const promise = new Promise((resolve, reject) => {
    if (typeof window === 'undefined' || !window.external?.sendMessage) {
      reject(new Error('当前不是桌面环境，无法选择保存文件夹'))
      return
    }
    const id = 'dm' + ++downloadManySeq + '-' + Date.now()
    desktopOps.downloadManyProgress = (msg) => onProgress?.(msg.file)
    desktopOps.downloadManyResult = (msg) => {
      // 多连接并发批量下载时结果可能串台：C# 回执带 id，只处理本次任务的结果
      if (msg.id && msg.id !== id) return
      delete desktopOps.downloadManyProgress
      if (msg.ok) resolve({ path: msg.path, count: msg.count })
      else if (msg.canceled) resolve(null)
      else reject(new Error(msg.innerError || msg.error || '批量下载失败'))
    }
    window.external.sendMessage(JSON.stringify({ op: 'downloadMany', id, url, paths }))
    cancelFn = () => {
      if (window.external?.sendMessage) {
        window.external.sendMessage(JSON.stringify({ op: 'downloadManyCancel', id }))
      }
    }
  })
  return { promise, cancel: () => cancelFn?.() }
}

// 桌面壳：弹原生「另存为」对话框选保存路径，由 C# 端从 url 流式下载到所选位置（不走 JS 桥传大文件）。
// 返回写入路径；用户取消返回 null；非桌面环境或出错 reject。浏览器端请走 <a download>。
// 下载耗时可能较长，不设超时，等 C# 回传结果。
export function desktopDownloadFile(url, defaultName) {
  return new Promise((resolve, reject) => {
    if (typeof window === 'undefined' || !window.external?.sendMessage) {
      reject(new Error('当前不是桌面环境，无法选择保存路径'))
      return
    }
    desktopOps.downloadFileResult = (msg) => {
      if (msg.ok) resolve(msg.path)
      else if (msg.canceled) resolve(null)
      else reject(new Error(msg.innerError || msg.error || '下载失败'))
    }
    window.external.sendMessage(JSON.stringify({ op: 'downloadFile', url, defaultName }))
  })
}

let droppedUploadSeq = 0
// 桌面壳（Linux GTK 拖放）：把拖入的本地文件/文件夹路径交给 C# 代读代传——浏览器 JS 无法读取任意
// 本地路径，由壳进程经本地 /api/ensure-dirs + /api/upload 上传到远端，进度走 uploadDroppedProgress。
// 返回 { promise, cancel }：promise 解析为 { count }；用户点「停止上传」解析 null；出错 reject。
export function desktopUploadDropped(baseUrl, connId, dir, paths, token, onProgress) {
  let cancelFn = null
  const promise = new Promise((resolve, reject) => {
    if (typeof window === 'undefined' || !window.external?.sendMessage) {
      reject(new Error('当前不是桌面环境，无法拖拽上传'))
      return
    }
    const id = 'up' + ++droppedUploadSeq + '-' + Date.now()
    // 进度/结果都是共享 key（同时只有一个拖拽上传在跑：前端 uploading 状态 + 激活标签限制），
    // C# 回执带 id，结果回调里按 id 过滤避免串台（与 downloadMany 同模式）
    desktopOps.uploadDroppedProgress = (msg) => onProgress?.(msg)
    desktopOps.uploadDroppedResult = (msg) => {
      if (msg.id && msg.id !== id) return
      delete desktopOps.uploadDroppedProgress
      if (msg.ok) resolve({ count: msg.count })
      else if (msg.canceled) resolve(null)
      else reject(new Error(msg.error || '拖拽上传失败'))
    }
    window.external.sendMessage(JSON.stringify({ op: 'uploadDropped', id, baseUrl, connId, dir, paths, token }))
    cancelFn = () => {
      if (window.external?.sendMessage) {
        window.external.sendMessage(JSON.stringify({ op: 'uploadDroppedCancel', id }))
      }
    }
  })
  return { promise, cancel: () => cancelFn?.() }
}

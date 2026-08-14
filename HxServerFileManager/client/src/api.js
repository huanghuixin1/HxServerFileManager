// 与后端 /api 交互的封装。所有接口均为同源（由 Kestrel 托管）。

async function request(url, options = {}) {
  const res = await fetch(url, {
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    ...options,
  })
  if (!res.ok) {
    let msg = `请求失败 (${res.status})`
    try {
      const body = await res.json()
      if (body && body.error) msg = body.error
    } catch (_) { /* ignore */ }
    throw new Error(msg)
  }
  // 204 / 无内容
  const ct = res.headers.get('content-type') || ''
  if (ct.includes('application/json')) return await res.json()
  return null
}

export const api = {
  health: () => request('/api/health'),

  connect: (req) =>
    request('/api/connect', { method: 'POST', body: JSON.stringify(req) }),

  disconnect: (connId) =>
    request('/api/disconnect', { method: 'POST', body: JSON.stringify({ connectionId: connId }) }),

  listConnections: () => request('/api/connections'),

  reconnect: (id) =>
    request('/api/connections/reconnect', { method: 'POST', body: JSON.stringify({ connectionId: id }) }),

  updateConnection: (id, req) =>
    request(`/api/connections/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: JSON.stringify(req),
    }),

  deleteConnection: (id) =>
    request(`/api/connections/${encodeURIComponent(id)}`, { method: 'DELETE' }),

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

  // 下载：返回可直接用于 <a download> 的 URL
  downloadUrl: (connId, path) =>
    `/api/download?connId=${encodeURIComponent(connId)}&path=${encodeURIComponent(path)}`,

  uploadFile: async (connId, path, file) => {
    const form = new FormData()
    form.append('connId', connId)
    form.append('path', path)
    form.append('file', file)
    const res = await fetch('/api/upload', { method: 'POST', body: form })
    if (!res.ok) {
      let msg = `上传失败 (${res.status})`
      try { const b = await res.json(); if (b && b.error) msg = b.error } catch (_) {}
      throw new Error(msg)
    }
    return await res.json()
  },

  getFileContent: (connId, path) =>
    request(`/api/file-content?connId=${encodeURIComponent(connId)}&path=${encodeURIComponent(path)}`),

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

  terminalInput: (connId, data) =>
    request('/api/terminal/input', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId, data }),
    }),

  terminalClose: (connId) =>
    request('/api/terminal/close', {
      method: 'POST',
      body: JSON.stringify({ connectionId: connId }),
    }),

  terminalStreamUrl: (connId) =>
    `/api/terminal/stream?connId=${encodeURIComponent(connId)}`,
}

// SSE 实时日志流
export function openLogStream(onMessage) {
  const es = new EventSource('/api/logs/stream')
  es.onmessage = (e) => {
    try {
      const entry = JSON.parse(e.data)
      onMessage(entry)
    } catch (_) { /* ignore */ }
  }
  es.onerror = () => { /* EventSource 会自动重连 */ }
  return es
}

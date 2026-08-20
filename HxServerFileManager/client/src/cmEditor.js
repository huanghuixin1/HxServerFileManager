// CodeMirror 6 编辑器封装（只被 EditorModal.vue 用 await import() 拉起）。
// 单独成一个模块的意义：所有 CodeMirror 代码会被 Vite 切进一个独立 chunk，
// 用户不点「编辑文件」就永远不下载（主包已经 1.5MB，别再往里堆）。
// 依赖面刻意压到最小：state / view / search / commands / language 五个核心包，
// 不引 autocomplete、lint，也不引 lezer 系语言包 —— 语法高亮走 legacy-modes 的单文件
// mode（每个几 KB，按扩展名再动态取一次）。
import { Annotation, Compartment, EditorState, Transaction } from '@codemirror/state'
import {
  EditorView, crosshairCursor, drawSelection, dropCursor, highlightActiveLine,
  highlightActiveLineGutter, highlightSpecialChars, keymap, lineNumbers, rectangularSelection,
} from '@codemirror/view'
import { highlightSelectionMatches, openSearchPanel, search, searchKeymap } from '@codemirror/search'
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands'
import {
  StreamLanguage, bracketMatching, defaultHighlightStyle, indentUnit, syntaxHighlighting,
} from '@codemirror/language'

// mode 加载器。注意必须写成字面量 import()：路径里带变量的话 Vite 静态分析不到，
// 打包后会运行时报错。
const mShell = () => import('@codemirror/legacy-modes/mode/shell').then((m) => m.shell)
const mNginx = () => import('@codemirror/legacy-modes/mode/nginx').then((m) => m.nginx)
const mProps = () => import('@codemirror/legacy-modes/mode/properties').then((m) => m.properties)
const mYaml = () => import('@codemirror/legacy-modes/mode/yaml').then((m) => m.yaml)
const mJson = () => import('@codemirror/legacy-modes/mode/javascript').then((m) => m.json)
const mJs = () => import('@codemirror/legacy-modes/mode/javascript').then((m) => m.javascript)
const mTs = () => import('@codemirror/legacy-modes/mode/javascript').then((m) => m.typescript)
const mPy = () => import('@codemirror/legacy-modes/mode/python').then((m) => m.python)
const mXml = () => import('@codemirror/legacy-modes/mode/xml').then((m) => m.xml)
const mHtml = () => import('@codemirror/legacy-modes/mode/xml').then((m) => m.html)
const mSql = () => import('@codemirror/legacy-modes/mode/sql').then((m) => m.standardSQL)
const mLua = () => import('@codemirror/legacy-modes/mode/lua').then((m) => m.lua)
const mToml = () => import('@codemirror/legacy-modes/mode/toml').then((m) => m.toml)
const mDiff = () => import('@codemirror/legacy-modes/mode/diff').then((m) => m.diff)
const mDocker = () => import('@codemirror/legacy-modes/mode/dockerfile').then((m) => m.dockerFile)
const mC = () => import('@codemirror/legacy-modes/mode/clike').then((m) => m.c)
const mCpp = () => import('@codemirror/legacy-modes/mode/clike').then((m) => m.cpp)
const mJava = () => import('@codemirror/legacy-modes/mode/clike').then((m) => m.java)
const mCs = () => import('@codemirror/legacy-modes/mode/clike').then((m) => m.csharp)

// 扩展名 -> mode。命中不了就不高亮（纯文本照样能编辑、能搜、有行号）。
const BY_EXT = {
  sh: mShell, bash: mShell, zsh: mShell, ksh: mShell, profile: mShell, bashrc: mShell,
  conf: mNginx, nginx: mNginx,
  ini: mProps, cfg: mProps, cnf: mProps, properties: mProps, env: mProps, service: mProps, repo: mProps,
  yml: mYaml, yaml: mYaml,
  json: mJson, js: mJs, mjs: mJs, cjs: mJs, ts: mTs,
  py: mPy,
  xml: mXml, xsl: mXml, plist: mXml, csproj: mXml, html: mHtml, htm: mHtml, vue: mHtml,
  sql: mSql, lua: mLua, toml: mToml, diff: mDiff, patch: mDiff,
  c: mC, h: mC, cpp: mCpp, cc: mCpp, hpp: mCpp, java: mJava, cs: mCs,
}
// 无扩展名的常见文件名
const BY_NAME = { dockerfile: mDocker, '.bashrc': mShell, '.profile': mShell, '.zshrc': mShell }

// 按路径挑语法高亮扩展；取不到就返回空扩展（不高亮，不报错）
async function languageFor(path) {
  const name = String(path || '').split(/[\\/]/).pop().toLowerCase()
  const loader = BY_NAME[name] || (name.includes('.') ? BY_EXT[name.split('.').pop()] : null)
  if (!loader) return []
  try {
    return StreamLanguage.define(await loader())
  } catch (_) {
    return []
  }
}

// 配色对齐 Element Plus 浅色风格（不引 theme-one-dark 那类额外包）
const theme = EditorView.theme({
  '&': { height: '100%', fontSize: '13px', backgroundColor: '#fff', color: '#2d3a4b' },
  '&.cm-focused': { outline: 'none' },
  '.cm-scroller': {
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
    lineHeight: '1.55',
  },
  '.cm-content': { padding: '6px 0 40px' }, // 底部留白：滚到底时最后一行不贴边
  '.cm-gutters': {
    backgroundColor: '#f5f7fa',
    color: '#a8b3c0',
    border: 'none',
    borderRight: '1px solid #e6eaf0',
  },
  '.cm-activeLineGutter': { backgroundColor: '#eaf1fb', color: '#5b6b7c' },
  '.cm-activeLine': { backgroundColor: '#f7fafd' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': { backgroundColor: '#d6e6ff' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: '#409eff' },
  '.cm-searchMatch': { backgroundColor: '#fff2a8', outline: '1px solid #e6c34a' },
  '.cm-searchMatch.cm-searchMatch-selected': { backgroundColor: '#ffc86b' },
  '.cm-selectionMatch': { backgroundColor: '#e8f1ff' },
  // 查找/跳转行面板：挤在弹窗里，字号和间距都收一收
  '.cm-panels': { backgroundColor: '#f5f7fa', color: '#5b6b7c' },
  '.cm-panels.cm-panels-top': { borderBottom: '1px solid #e6eaf0' },
  '.cm-panel.cm-search': { padding: '6px 8px', fontSize: '12px' },
  '.cm-panel.cm-search input, .cm-panel.cm-gotoLine input': {
    border: '1px solid #dcdfe6',
    borderRadius: '3px',
    padding: '2px 6px',
    fontSize: '12px',
    outline: 'none',
  },
  '.cm-panel.cm-search input:focus': { borderColor: '#409eff' },
  '.cm-panel.cm-search button, .cm-panel.cm-gotoLine button': {
    backgroundImage: 'none',
    backgroundColor: '#fff',
    border: '1px solid #dcdfe6',
    borderRadius: '3px',
    padding: '2px 8px',
    fontSize: '12px',
    cursor: 'pointer',
  },
  '.cm-panel.cm-search button:hover': { borderColor: '#409eff', color: '#409eff' },
  '.cm-panel.cm-search label': { fontSize: '12px' },
  '.cm-panel.cm-search [name="close"]': { fontSize: '16px', padding: '0 6px' },
})

// 标记「程序化写入」的事务：加载期边收边填的内容既不该进撤销栈，
// 也不该被当成用户改动（否则一打开就显示未保存）。
const silentWrite = Annotation.define()

/**
 * 建一个编辑器实例。返回的 handle 只暴露组件用得到的动作，
 * 调用方不需要碰 CodeMirror 的 transaction 细节。
 * @param {Element} parent 挂载容器
 * @param {boolean} readOnly 初始是否只读（加载期置真，读完解锁）
 * @param {Function} onSave Ctrl+S 回调
 * @param {Function} onChange 用户改动回调（程序化写入不触发）
 */
export function createEditor({ parent, readOnly = false, onSave, onChange }) {
  const roComp = new Compartment()
  const wrapComp = new Compartment()
  const langComp = new Compartment()
  const view = new EditorView({
    parent,
    state: EditorState.create({
      extensions: [
        lineNumbers(),
        highlightActiveLine(),
        highlightActiveLineGutter(),
        highlightSpecialChars(),
        drawSelection(),
        dropCursor(),
        rectangularSelection(),
        crosshairCursor(),
        history(),
        bracketMatching(),
        indentUnit.of('    '), // 4 空格，与原 textarea 的 tab-size: 4 观感一致
        search({ top: true }), // 查找面板放顶部，避免被弹窗 footer 压住
        highlightSelectionMatches(),
        syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
        keymap.of([
          { key: 'Mod-s', preventDefault: true, run: () => { onSave?.(); return true } },
          indentWithTab, // Tab 缩进而不是跳焦点（要跳焦点先按 Esc）
          ...searchKeymap, // Ctrl+F 查找、Ctrl+H 替换、Alt+G 跳转行、F3 下一个
          ...historyKeymap,
          ...defaultKeymap,
        ]),
        roComp.of(EditorState.readOnly.of(readOnly)),
        wrapComp.of([]),
        langComp.of([]),
        theme,
        EditorView.updateListener.of((u) => {
          if (!u.docChanged) return
          if (u.transactions.some((t) => t.annotation(silentWrite))) return
          onChange?.()
        }),
      ],
    }),
  })

  // 弹窗随时可能被关掉（view.destroy()），而 setLanguage 是异步的 ——
  // destroy 之后再 dispatch 会抛异常，这里统一挡掉。
  let dead = false
  const send = (spec) => {
    if (dead) return
    view.dispatch(spec)
  }
  const write = (changes) =>
    send({ changes, annotations: [silentWrite.of(true), Transaction.addToHistory.of(false)] })

  return {
    view,
    // 流式加载：增量追加，不整段重建（几 MB 的日志也不卡）
    append: (text) => { if (text) write({ from: view.state.doc.length, insert: text }) },
    setDoc: (text) => write({ from: 0, to: view.state.doc.length, insert: text ?? '' }),
    getDoc: () => view.state.doc.toString(),
    lines: () => view.state.doc.lines,
    setReadOnly: (v) => send({ effects: roComp.reconfigure(EditorState.readOnly.of(v)) }),
    setWrap: (v) => send({ effects: wrapComp.reconfigure(v ? EditorView.lineWrapping : []) }),
    setLanguage: async (path) => {
      const ext = await languageFor(path)
      send({ effects: langComp.reconfigure(ext) })
    },
    openSearch: () => { if (!dead) { view.focus(); openSearchPanel(view) } },
    focus: () => { if (!dead) view.focus() },
    destroy: () => { dead = true; view.destroy() },
  }
}



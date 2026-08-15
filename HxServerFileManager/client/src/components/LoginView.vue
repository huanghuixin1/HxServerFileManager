<script setup>
import { ref, onMounted } from 'vue'
import { api } from '../api.js'

const emit = defineEmits(['authed'])

const password = ref('')
const remember = ref(true)
const loading = ref(false)
const error = ref('')

// 记住密码：勾选「记住我」登录成功后把密码存本机，下次回到登录页自动回填。
// 明文存 localStorage（本工具已有 connections.json 明文凭据的先例，仅限本地/内网）；
// 登出不删——不然 token 过期/登出后还得重输，记住就没意义了。
const PWD_KEY = 'hxsfm_remember_pwd'
function loadSavedPwd() {
  const saved = localStorage.getItem(PWD_KEY)
  if (saved) password.value = saved
}
onMounted(loadSavedPwd)

async function submit() {
  if (!password.value) {
    error.value = '请输入访问密码'
    return
  }
  loading.value = true
  error.value = ''
  try {
    const res = await api.login(password.value, remember.value)
    // 记住密码：勾选则存，取消勾选则清掉
    if (remember.value) localStorage.setItem(PWD_KEY, password.value)
    else localStorage.removeItem(PWD_KEY)
    emit('authed', { token: res.token, remember: remember.value })
  } catch (e) {
    if (e.locked) {
      error.value = '密码错误次数过多，账号已被临时锁定，请稍后再试'
    } else if (e.remainingAttempts != null && e.remainingAttempts >= 0) {
      error.value = `密码错误，剩余尝试次数 ${e.remainingAttempts}`
    } else {
      error.value = e.message
    }
    password.value = ''
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="login-wrap">
    <div class="login-card">
      <div class="login-logo">
        <el-icon :size="30"><Monitor /></el-icon>
      </div>
      <h1 class="login-title">HxServerFileManager</h1>
      <p class="login-sub">输入访问密码以继续</p>

      <el-input
        v-model="password"
        type="password"
        show-password
        placeholder="访问密码"
        size="large"
        :disabled="loading"
        @keyup.enter="submit"
      />

      <div class="login-row">
        <el-checkbox v-model="remember">记住密码（本机免重复输入）</el-checkbox>
      </div>

      <el-button
        class="login-btn"
        type="primary"
        size="large"
        :loading="loading"
        @click="submit"
      >
        登 录
      </el-button>

      <el-alert
        v-if="error"
        :title="error"
        type="error"
        :closable="false"
        show-icon
        class="login-error"
      />
    </div>
  </div>
</template>

<style scoped>
.login-wrap {
  height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: linear-gradient(135deg, #eef3fb 0%, #f7f9fc 100%);
}
.login-card {
  width: min(380px, 90vw);
  background: #fff;
  border: 1px solid var(--border);
  border-radius: 14px;
  box-shadow: 0 10px 40px rgba(31, 45, 61, 0.08);
  padding: 36px 32px 28px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.login-logo {
  display: flex;
  justify-content: center;
  color: #2d6cdf;
}
.login-title {
  text-align: center;
  font-size: 20px;
  color: #1f2d3d;
  margin: 0;
}
.login-sub {
  text-align: center;
  font-size: 13px;
  color: #8a97a5;
  margin: -6px 0 6px;
}
.login-row {
  display: flex;
  justify-content: flex-start;
}
.login-btn {
  width: 100%;
  font-size: 15px;
  letter-spacing: 6px;
}
.login-error {
  margin-top: 4px;
}
</style>

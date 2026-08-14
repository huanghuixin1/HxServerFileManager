# 一个最小但可用的 Linux 测试机（Ubuntu 22.04 + OpenSSH）
# 用途：作为你 Web 服务的 SSH/SFTP 连接测试目标。
FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        openssh-server \
        sudo \
        ca-certificates \
        curl \
        vim-tiny \
        htop && \
    mkdir -p /var/run/sshd && \
    # 创建测试用户 testuser，密码 testpass，并赋予免密 sudo
    useradd -m -s /bin/bash testuser && \
    echo "testuser:testpass" | chpasswd && \
    echo "testuser ALL=(ALL) NOPASSWD:ALL" >> /etc/sudoers && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# 允许 root 也用密码登录（可选，方便测试；生产勿开）
RUN echo "root:rootpass" | chpasswd

EXPOSE 22

# 以前台方式一直运行 sshd（-D 不后台化，保证容器不退出）
CMD ["/usr/sbin/sshd", "-D"]

#!/bin/bash
set -euo pipefail

DATA_DIR="/var/lib/mysql"
INIT_SCRIPT="/docker-entrypoint-initdb.d/1-schema.sql"

MYSQL_ROOT_PASSWORD=${MYSQL_ROOT_PASSWORD:-rootpass}
MYSQL_DATABASE=${MYSQL_DATABASE:-syntara}
MYSQL_USER=${MYSQL_USER:-hwid}
MYSQL_PASSWORD=${MYSQL_PASSWORD:-hwidpass}

if [[ ! -d "${DATA_DIR}/mysql" ]]; then
  echo "[init] Initialising MariaDB data directory..."
  mariadb-install-db --user=mysql --ldata="${DATA_DIR}" >/dev/null
fi

echo "[init] Starting MariaDB..."
mysqld_safe --bind-address=0.0.0.0 --skip-name-resolve --skip-networking=0 &
MYSQL_PID=$!

cleanup() {
  echo "[shutdown] Stopping services..."
  if mysqladmin --user=root --password="${MYSQL_ROOT_PASSWORD}" shutdown >/dev/null 2>&1; then
    :
  else
    kill "${MYSQL_PID}" >/dev/null 2>&1 || true
  fi
  wait "${MYSQL_PID}" >/dev/null 2>&1 || true
  if [[ -n "${PHP_PID:-}" ]]; then
    kill "${PHP_PID}" >/dev/null 2>&1 || true
    wait "${PHP_PID}" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT INT TERM

echo -n "[init] Waiting for MariaDB to accept connections"
until mariadb-admin ping --silent >/dev/null 2>&1; do
  echo -n "."
  sleep 1
done
echo

echo "[init] Configuring database users..."

ROOT_ARGS=(-u root)
if mariadb "${ROOT_ARGS[@]}" -e "SELECT 1" >/dev/null 2>&1; then
  AUTH_MODE="nopass"
else
  ROOT_ARGS=(-u root "-p${MYSQL_ROOT_PASSWORD}")
  if ! mariadb "${ROOT_ARGS[@]}" -e "SELECT 1" >/dev/null 2>&1; then
    echo "[init] ERROR: Unable to authenticate as root with or without password." >&2
    exit 1
  fi
fi

mariadb "${ROOT_ARGS[@]}" <<-SQL
  ALTER USER 'root'@'localhost' IDENTIFIED BY '${MYSQL_ROOT_PASSWORD}';
  CREATE DATABASE IF NOT EXISTS \`${MYSQL_DATABASE}\`;
  CREATE USER IF NOT EXISTS '${MYSQL_USER}'@'%' IDENTIFIED BY '${MYSQL_PASSWORD}';
  GRANT ALL PRIVILEGES ON \`${MYSQL_DATABASE}\`.* TO '${MYSQL_USER}'@'%';
  FLUSH PRIVILEGES;
SQL

if [[ -f "${INIT_SCRIPT}" ]]; then
  echo "[init] Applying schema from ${INIT_SCRIPT} (if needed)..."
  if ! mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "SHOW TABLES LIKE 'users';" | grep -q "users"; then
    mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" "${MYSQL_DATABASE}" < "${INIT_SCRIPT}"
  else
    echo "[init] Schema already present, checking for missing tables..."
    mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" "${MYSQL_DATABASE}" <<-SQL
      CREATE TABLE IF NOT EXISTS admin_action_logs (
        id BIGINT AUTO_INCREMENT PRIMARY KEY,
        admin_id BIGINT NOT NULL,
        action_type VARCHAR(50) NOT NULL,
        target_type VARCHAR(50) NULL,
        target_id VARCHAR(255) NULL,
        details TEXT NULL,
        created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        INDEX idx_admin_id (admin_id),
        INDEX idx_action_type (action_type),
        INDEX idx_created_at (created_at)
      );
SQL
  fi
fi

# Apply incremental schema updates
if mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "SHOW TABLES LIKE 'users';" | grep -q "users"; then
  if ! mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "SHOW COLUMNS FROM users LIKE 'last_key';" | grep -q "last_key"; then
    echo "[init] Adding missing column users.last_key..."
    mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "ALTER TABLE users ADD COLUMN last_key VARCHAR(255) NULL AFTER banned;"
  fi
fi

if mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "SHOW TABLES LIKE 'subscriptions';" | grep -q "subscriptions"; then
  if ! mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "SHOW COLUMNS FROM subscriptions LIKE 'expires_at';" | grep -q "expires_at"; then
    echo "[init] Adding missing column subscriptions.expires_at..."
    mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "ALTER TABLE subscriptions ADD COLUMN expires_at DATETIME NULL AFTER days;"
  fi
fi

# Create payment_requests table
echo "[init] Checking for payment_requests table..."
if ! mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "SHOW TABLES LIKE 'payment_requests';" | grep -q "payment_requests"; then
  echo "[init] Creating payment_requests table..."
  mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" <<-SQL
    CREATE TABLE IF NOT EXISTS payment_requests (
      id INT AUTO_INCREMENT PRIMARY KEY,
      user_id BIGINT NOT NULL,
      days INT NOT NULL,
      amount DECIMAL(10, 2) NOT NULL,
      product_name VARCHAR(255) NOT NULL,
      created_at DATETIME NOT NULL,
      status VARCHAR(20) NOT NULL DEFAULT 'pending',
      approved_by BIGINT NULL,
      processed_at DATETIME NULL,
      INDEX idx_user_id (user_id),
      INDEX idx_status (status)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
SQL
fi

# Apply product versions and update notifications tables
echo "[init] Checking for product_versions and update_notifications tables..."
if ! mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "SHOW TABLES LIKE 'product_versions';" | grep -q "product_versions"; then
  echo "[init] Creating product_versions table..."
  mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" <<-SQL
    CREATE TABLE IF NOT EXISTS product_versions (
      id BIGINT AUTO_INCREMENT PRIMARY KEY,
      version VARCHAR(50) NOT NULL,
      file_id VARCHAR(255) NOT NULL,
      file_name VARCHAR(255) NOT NULL,
      file_size BIGINT NOT NULL,
      update_log TEXT NULL,
      uploaded_by BIGINT NOT NULL,
      created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
      is_latest BOOLEAN NOT NULL DEFAULT TRUE,
      INDEX idx_is_latest (is_latest),
      INDEX idx_created_at (created_at)
    );
SQL
fi

if ! mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "SHOW TABLES LIKE 'update_notifications';" | grep -q "update_notifications"; then
  echo "[init] Creating update_notifications table..."
  mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" <<-SQL
    CREATE TABLE IF NOT EXISTS update_notifications (
      id BIGINT AUTO_INCREMENT PRIMARY KEY,
      version_id BIGINT NOT NULL,
      user_id BIGINT NOT NULL,
      notified_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
      downloaded BOOLEAN NOT NULL DEFAULT FALSE,
      downloaded_at DATETIME NULL,
      FOREIGN KEY (version_id) REFERENCES product_versions(id) ON DELETE CASCADE,
      INDEX idx_user_version (user_id, version_id),
      INDEX idx_notified_at (notified_at)
    );
SQL
fi

if ! mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" -e "SHOW TABLES LIKE 'subscription_keys';" | grep -q "subscription_keys"; then
  echo "[init] Creating subscription_keys table..."
  mariadb -u"${MYSQL_USER}" -p"${MYSQL_PASSWORD}" -D "${MYSQL_DATABASE}" <<-SQL
    CREATE TABLE IF NOT EXISTS subscription_keys (
      id BIGINT AUTO_INCREMENT PRIMARY KEY,
      user_id BIGINT NOT NULL,
      key_value VARCHAR(50) NOT NULL UNIQUE,
      days INT NOT NULL,
      expires_at DATETIME NULL,
      created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
      INDEX idx_user_id (user_id),
      INDEX idx_key_value (key_value)
    );
SQL
fi

echo "[init] Starting PHP built-in server on 0.0.0.0:8080..."
php -S 0.0.0.0:8080 -t /srv/app &
PHP_PID=$!

wait "${PHP_PID}"

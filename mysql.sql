-- Создание базы данных
CREATE DATABASE IF NOT EXISTS syntara;

-- Использование базы данных
USE syntara;

-- Создание таблицы для подписок
CREATE TABLE IF NOT EXISTS subscriptions (
    id BIGINT NOT NULL,
    subscription_key VARCHAR(255) NOT NULL UNIQUE,
    days INT NOT NULL,
    expires_at DATETIME NULL,
    PRIMARY KEY (subscription_key)
);

-- Создание таблицы для пользователей
CREATE TABLE IF NOT EXISTS users (
    id BIGINT NOT NULL,
    hwid VARCHAR(255) NOT NULL UNIQUE,
    subscription DATETIME NOT NULL,
    banned BOOLEAN NOT NULL DEFAULT 0,
    last_key VARCHAR(255) DEFAULT NULL,
    PRIMARY KEY (id, hwid)
);

-- Таблица заявок на сброс HWID
CREATE TABLE IF NOT EXISTS hwid_reset_requests (
    id INT AUTO_INCREMENT PRIMARY KEY,
    user_id BIGINT NOT NULL,
    hwid VARCHAR(255) NOT NULL,
    status ENUM('pending', 'approved', 'rejected') NOT NULL DEFAULT 'pending',
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Таблица логов действий админа
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
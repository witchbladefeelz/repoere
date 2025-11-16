-- Таблица для хранения информации о версиях продукта
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

-- Таблица для отслеживания уведомлений об обновлениях
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

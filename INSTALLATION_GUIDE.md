# 🚀 Руководство по установке системы загрузки

## Шаг 1: Обновление базы данных

Выполните SQL скрипт для создания новых таблиц:

```bash
mysql -u hwid -phwidpass syntara < updates_schema.sql
```

Или вручную через MySQL:

```sql
USE syntara;

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
```

## Шаг 2: Сборка проекта

```bash
cd C:\Users\artem\OneDrive\Desktop\hwid\src\HwidBots.MultiBot
dotnet build
```

## Шаг 3: Запуск ботов

```bash
dotnet run
```

Или в Release режиме:

```bash
dotnet build -c Release
dotnet run -c Release
```

## Шаг 4: Проверка работы

### Проверка Admin Bot

1. Откройте админ-бота в Telegram
2. Отправьте `/start`
3. Вы должны увидеть новые кнопки:
   - `📦 Upload Update`
   - `📚 Version History`

### Проверка User Bot

1. Откройте пользовательского бота
2. Отправьте `/start`
3. Если у вас есть активная подписка, вы увидите кнопку `📥 Download`

## Шаг 5: Загрузка первой версии

1. В админ-боте нажмите `📦 Upload Update`
2. Введите версию, например: `1.0.0`
3. Введите changelog или `skip`
4. Отправьте файл как документ (до 50 MB)
5. Дождитесь подтверждения

## Шаг 6: Тестирование скачивания

1. В пользовательском боте (с активной подпиской)
2. Нажмите `📥 Download`
3. Вы должны получить файл

## Проверка таблиц

Убедитесь, что таблицы созданы:

```sql
USE syntara;
SHOW TABLES LIKE 'product%';
SHOW TABLES LIKE 'update%';
```

Должны быть:
- `product_versions`
- `update_notifications`

## Возможные проблемы

### Ошибка: Table doesn't exist

**Решение:** Выполните SQL скрипт из Шага 1

### Ошибка: File too large

**Решение:** Telegram боты могут отправлять файлы до 50 MB. Для больших файлов используйте архивы.

### Кнопка Download не появляется

**Решение:**
1. Проверьте, что у пользователя есть активная подписка
2. Проверьте SQL запрос в `HasActiveSubscriptionOrKeysAsync`

### Уведомления не отправляются

**Решение:**
1. Проверьте логи бота
2. Убедитесь, что есть активные пользователи
3. Проверьте метод `GetActiveUserIdsAsync`

## Полезные SQL запросы

### Проверить активных пользователей

```sql
SELECT DISTINCT u.id, u.hwid, u.subscription
FROM users u
LEFT JOIN subscriptions s ON u.id = s.id
WHERE (u.subscription > NOW() OR (s.expires_at IS NULL OR s.expires_at > NOW()))
  AND u.banned = 0;
```

### Проверить загруженные версии

```sql
SELECT * FROM product_versions ORDER BY created_at DESC;
```

### Статистика скачиваний

```sql
SELECT
    pv.version,
    COUNT(DISTINCT un.user_id) AS notified,
    SUM(CASE WHEN un.downloaded = 1 THEN 1 ELSE 0 END) AS downloaded
FROM product_versions pv
LEFT JOIN update_notifications un ON pv.id = un.version_id
GROUP BY pv.id, pv.version
ORDER BY pv.created_at DESC;
```

## Готово! 🎉

Система загрузки и обновлений установлена и готова к использованию.

Для получения дополнительной информации см. `DOWNLOAD_SYSTEM.md`

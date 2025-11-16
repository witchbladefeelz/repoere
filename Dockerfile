FROM debian:12-slim

ENV DEBIAN_FRONTEND=noninteractive \
    MYSQL_ROOT_PASSWORD=rootpass \
    MYSQL_DATABASE=syntara \
    MYSQL_USER=hwid \
    MYSQL_PASSWORD=hwidpass \
    TZ=UTC

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        mariadb-server mariadb-client \
        php-cli php-mysql \
        curl ca-certificates \
        tini tzdata && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /srv/app

COPY php/ /srv/app/
COPY mysql.sql /docker-entrypoint-initdb.d/1-schema.sql
COPY start.sh /usr/local/bin/start.sh

RUN chmod +x /usr/local/bin/start.sh

VOLUME ["/var/lib/mysql"]

EXPOSE 8080 3306

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["/usr/local/bin/start.sh"]


# ---- Build Stage ----
FROM node:22-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm install

COPY . .
RUN npm run build:prod

# ---- Runtime Stage ----
FROM nginx:alpine AS runtime

RUN addgroup -S appgroup && adduser -S appuser -G appgroup

COPY --from=build /app/dist/poc-aura-frontend/browser /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

RUN chown -R appuser:appgroup /usr/share/nginx/html \
    && chown -R appuser:appgroup /var/cache/nginx \
    && chown -R appuser:appgroup /var/log/nginx \
    && touch /var/run/nginx.pid \
    && chown appuser:appgroup /var/run/nginx.pid

USER appuser

EXPOSE 4200
CMD ["nginx", "-g", "daemon off;"]

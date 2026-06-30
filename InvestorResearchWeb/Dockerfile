# syntax=docker/dockerfile:1.7

FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM node:24-alpine AS runtime
LABEL org.opencontainers.image.title="investor-research-web" \
      org.opencontainers.image.description="Investor research dashboard with OAuth-gated OpenAI analysis API" \
      org.opencontainers.image.source="D:/거래플랜/InvestorResearchWeb"
WORKDIR /app
ENV NODE_ENV=production \
    PORT=8080
COPY package.json package-lock.json ./
RUN npm ci --omit=dev
COPY --from=build /app/dist ./dist
COPY server ./server
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:8080/healthz').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
CMD ["node", "server/index.mjs"]

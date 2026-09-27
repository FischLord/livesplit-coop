FROM node:24-alpine
WORKDIR /app
COPY package*.json ./
RUN npm ci --omit=dev --ignore-scripts && npm cache clean --force
COPY src ./src
USER node
ENV BIND=0.0.0.0 PORT=8787 ROOMS_FILE=/run/secrets/rooms.json
EXPOSE 8787
CMD ["node", "src/main.mjs"]

# syntax=docker/dockerfile:1

FROM golang:1.22-alpine AS build

WORKDIR /src
COPY go.mod ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/router-server .

FROM alpine:3.20

RUN addgroup -S pixelwizard && adduser -S pixelwizard -G pixelwizard
USER pixelwizard

WORKDIR /app
COPY --from=build /out/router-server /app/router-server

EXPOSE 9000

ENTRYPOINT ["/app/router-server"]

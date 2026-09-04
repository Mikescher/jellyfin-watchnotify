# syntax=docker/dockerfile:1

FROM golang:1.26 AS build
WORKDIR /src
COPY go.mod ./
RUN go mod download
COPY *.go ./
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /app .

# distroless/static includes CA certs (for SCN HTTPS) and tzdata, and its
# default user is root so binding to port 80 works.
FROM gcr.io/distroless/static-debian12:latest
COPY --from=build /app /app
ENV PORT=80
EXPOSE 80
ENTRYPOINT ["/app"]

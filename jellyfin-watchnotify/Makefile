IMAGE     = registry.blackforestbytes.com/mikescher/jellyfin-watchnotify
TAG       = latest
PLATFORMS = linux/amd64,linux/arm64
BUILDER   = jellyfin-watchnotify-builder

.PHONY: run build vet docker push-docker

run:
	@set -a; [ -f .env ] && . ./.env || true; set +a; go run .

build:
	CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o jellyfin-watchnotify .

vet:
	go vet ./...

docker:
	docker build -t $(IMAGE):$(TAG) .

buildx-setup:
	@docker buildx inspect $(BUILDER) >/dev/null 2>&1 || \
	  docker buildx create --name $(BUILDER) --driver docker-container --bootstrap

docker-multiarch: buildx-setup
	docker buildx build --builder $(BUILDER) --platform $(PLATFORMS) -t $(IMAGE):$(TAG) .

push-docker: buildx-setup
	docker buildx build --builder $(BUILDER) --platform $(PLATFORMS) -t $(IMAGE):$(TAG) --push .

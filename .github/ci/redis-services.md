# Redis service images

Workflow Redis services use Docker Official Images from
[`public.ecr.aws/docker/library/redis`](https://gallery.ecr.aws/docker/library/redis),
pinned by SHA-256 digest. ECR Public supports
[unauthenticated pulls by digest](https://docs.aws.amazon.com/AmazonECR/latest/public/docker-pull-ecr-image.html),
so pull requests from forks need no registry credentials.

GitHub Actions pulls `jobs.<job>.services` images before it runs any steps. An
explicit `mirror.gcr.io/library/redis` service therefore cannot use the fallback
configured later by `.github/actions/docker-hub-mirror`. A quota incident at that
mirror failed all initialization retries in
[run 39650373498](https://github.com/thomhurst/Respire/actions/runs/39650373498/job/119396818896)
before the generated mapper Native AOT job reached checkout. Using ECR Public for
these services removes that dependency on the Google mirror's quota. ECR Public
still has its own availability and quota limits; this is a separate source, not
an automatic failover mechanism.

The service audit covers all 26 Redis service declarations in 24 workflows:

- CI: VectorData, generated mapper Native AOT, and the ASP.NET Core sample,
  including both framework legs where present.
- Benchmark services: every `services.redis` declaration, including the optional
  service expression in the reusable comparison workflow and the scheduled
  comparison workflow.
- Stress tests: the service shared by the selectable framework runs.

The Alpine service keeps digest
`sha256:3811787313eba226a2ef38658c6ccb91cd5e110edc89c37767de373120a0e5a0`.
The standard service uses the existing common benchmark pin,
`sha256:5c02d7c0f8d75af45958c0294a8004f2e14b42df98a1103e3a669aa7ec787be6`.
The three formerly tag-only service declarations now use that standard pin too.
The reusable comparison's older `2c2dff79…` digest failed manifest verification
on both the Google mirror and ECR Public during the audit, so it also uses the
verified common benchmark pin. These are upstream Docker Official Images, not
repository-owned rebuilds. Keep tag and digest together when updating images;
never drop a digest to work around a registry outage.

Testcontainers still uses the existing Docker Hub mirror action and its
daemon-level fallback. The response-routing and Unix-socket benchmarks start
Redis inside steps after configuring that action. Their explicit Google mirror
references are replaced with canonical `redis:` references, preserving their
existing versions and pins so Docker can fall back to Docker Hub if its mirror
fails. They do not need the pre-step ECR service source.

## Validation

`scripts/workflows/DockerHubMirrorTests.py`, run by CI's workflow-policy job,
audits every Redis service reference (including conditional expressions) for the
ECR source and a full digest pin. To run it locally:

```sh
python -B -m unittest discover -s scripts/workflows -p DockerHubMirrorTests.py
```

Before changing a pin, pull the exact reference from ECR Public and initialize
a disposable service with the workflow's health check. For example:

```sh
image=public.ecr.aws/docker/library/redis:8.10-alpine@sha256:3811787313eba226a2ef38658c6ccb91cd5e110edc89c37767de373120a0e5a0
docker pull "$image"
docker run -d --name respire-redis-service-check -p 127.0.0.1::6379 \
  --health-cmd 'redis-cli ping' --health-interval 1s \
  --health-timeout 3s --health-retries 30 "$image"
docker exec respire-redis-service-check redis-cli ping
docker inspect --format '{{.State.Health.Status}}' respire-redis-service-check
docker rm -f respire-redis-service-check
```

Repeat for the standard service reference when updating its pin. Both pinned
variants were pulled from ECR Public and initialized locally for issue #2147;
each answered `PONG` and reached `healthy` using the service health check.

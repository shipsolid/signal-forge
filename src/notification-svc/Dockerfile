FROM python:3.12-slim@sha256:520153e2deb359602c9cffd84e491e3431d76e7bf95a3255c9ce9433b76ab99a

WORKDIR /app

# Install corporate CA (Zscaler) so pip can reach PyPI over SSL.
# zcert.crt is copied to the build context by the Makefile build target.
COPY zcert.crt /usr/local/share/ca-certificates/zcert.crt
RUN update-ca-certificates

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app/ ./app/

# Non-root UID 1000, matches k8s securityContext.runAsUser.
RUN groupadd --system --gid 1000 app \
 && useradd  --system --uid 1000 --gid 1000 --home /app --shell /sbin/nologin app \
 && chown -R 1000:1000 /app
USER 1000:1000

ENV PYTHONUNBUFFERED=1
ENV OTEL_LOGS_EXPORTER=none

EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]

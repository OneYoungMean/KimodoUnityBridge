FROM nvcr.io/nvidia/pytorch:24.10-py3

# Avoid some interactive prompts + make pip quieter/reproducible-ish
ENV DEBIAN_FRONTEND=noninteractive \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

# Where your code will live inside the container
WORKDIR /workspace

# System deps
RUN apt-get update && apt-get install -y --no-install-recommends \
      git curl ca-certificates \
      cmake build-essential \
      gosu \
    && rm -rf /var/lib/apt/lists/*

# Some base images ship a broken `/usr/local/bin/cmake` shim (from a partial pip install),
# which shadows `/usr/bin/cmake` and breaks builds that invoke `cmake` (e.g. MotionCorrection).
# Prefer the system cmake.
RUN rm -f /usr/local/bin/cmake || true

# Install from docker_requirements.txt: kimodo editable (-e .),
# but MotionCorrection non-editable (./MotionCorrection). The -e . line ensures [project.scripts]
# from pyproject.toml are installed (kimodo_gen, kimodo_demo, kimodo_textencoder).
# SKIP_MOTION_CORRECTION_IN_SETUP=1 so setup.py does not bundle motion_correction; it is
# installed separately from ./MotionCorrection in the requirements file (non-editable).
COPY docker_requirements.txt /workspace/docker_requirements.txt
COPY setup.py /workspace/setup.py
COPY pyproject.toml /workspace/pyproject.toml
COPY kimodo /workspace/kimodo
COPY kimodo-viser /workspace/kimodo-viser
COPY MotionCorrection /workspace/MotionCorrection

RUN --mount=type=cache,target=/root/.cache/pip \
    python -m pip install --upgrade pip \
 && SKIP_MOTION_CORRECTION_IN_SETUP=1 python -m pip install -r docker_requirements.txt

# Use the docker-entrypoint script, to allow the docker to run as the actual user instead of root
COPY kimodo/scripts/docker-entrypoint.sh /usr/local/bin/docker-entrypoint
RUN chmod +x /usr/local/bin/docker-entrypoint

# Default command (change to your entrypoint if you have one)
ENTRYPOINT ["docker-entrypoint"]
CMD ["bash"]

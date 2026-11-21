# Installation Guide for SAM 3D Body

## Setup Python Environment

### 1. Create and Activate Environment

```bash
conda create -n sam_3d_body python=3.11 -y
conda activate sam_3d_body
```

### 2. Install PyTorch

Please install PyTorch following the [official instructions](https://pytorch.org/get-started/locally/).

### 3. Install Python Dependencies

```bash
pip install pytorch-lightning pyrender opencv-python yacs scikit-image einops timm dill pandas rich hydra-core hydra-submitit-launcher hydra-colorlog pyrootutils webdataset chump networkx==3.2.1 roma joblib seaborn wandb appdirs appnope ffmpeg cython jsonlines pytest xtcocotools loguru optree fvcore black pycocotools tensorboard huggingface_hub
```

### 4. Install Detectron2

```bash
pip install 'git+https://github.com/facebookresearch/detectron2.git@a1ce2f9' --no-build-isolation --no-deps
```

### 5. Install MoGe (Optional)

```bash
pip install git+https://github.com/microsoft/MoGe.git
```

### 6. Install SAM3 (Optional)
```bash
# this is a minimal installation of sam3 only to support its inference 
git clone https://github.com/facebookresearch/sam3.git
cd sam3
pip install -e .
pip install decord psutil
```


## Getting Model Checkpoints

We host model checkpoints on Hugging Face. **Available models:**
- [`facebook/sam-3d-body-dinov3`](https://huggingface.co/facebook/sam-3d-body-dinov3)
- [`facebook/sam-3d-body-vith`](https://huggingface.co/facebook/sam-3d-body-vith)


⚠️ Please note that you need to **request access** on the SAM 3D Body Hugging Face repos above. Once accepted, you need to be authenticated to download the checkpoints.

⚠️ SAM 3D Body is available via HuggingFace globally, **except** in comprehensively sanctioned jurisdictions. Sanctioned jurisdiction will result in requests being **rejected**.


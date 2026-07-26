import os
# DO NOT import transformers or any other library here
from huggingface_hub import HfApi

# 1. CONFIGURATION
# USE YOUR NEW TOKEN HERE AFTER REFRESHING IT
TOKEN = "" 
REPO_ID = "Aero-Ex/KIMODO-Meta3_llm2vec_Baked"
LOCAL_FOLDER = "/teamspace/studios/this_studio/kimodo/models/KIMODO-Meta3_llm2vec-SUPER_BAKED"

api = HfApi(token=TOKEN)

def upload_models():
    if not os.path.exists(LOCAL_FOLDER):
        print(f"[!] Error: {LOCAL_FOLDER} not found.")
        return

    print(f"[*] Initializing Repo: {REPO_ID}")
    try:
        api.create_repo(repo_id=REPO_ID, repo_type="model", exist_ok=True)
        
        print(f"[*] Starting upload. This works with LFS and handles large GGUF files.")
        print("[*] Please wait, this may take a long time without a progress bar...")
        
        # We use standard upload_folder which is robust across hub versions
        api.upload_folder(
            folder_path=LOCAL_FOLDER,
            repo_id=REPO_ID,
            repo_type="model",
            commit_message="Modular TRELLIS-2 GGUF suite - Fixed Structure"
        )
        
        print(f"\n[SUCCESS] Suite is now live at: https://huggingface.co/{REPO_ID}")
        
    except Exception as e:
        print(f"\n[ERROR] Upload failed: {str(e)}")

if __name__ == "__main__":
    upload_models()
import argparse
import json
from pathlib import Path

import torch


SKELETON_LAYOUTS = {
    "somaskel30": [
        ("Hips", None),
        ("Spine1", "Hips"),
        ("Spine2", "Spine1"),
        ("Chest", "Spine2"),
        ("Neck1", "Chest"),
        ("Neck2", "Neck1"),
        ("Head", "Neck2"),
        ("Jaw", "Head"),
        ("LeftEye", "Head"),
        ("RightEye", "Head"),
        ("LeftShoulder", "Chest"),
        ("LeftArm", "LeftShoulder"),
        ("LeftForeArm", "LeftArm"),
        ("LeftHand", "LeftForeArm"),
        ("LeftHandThumbEnd", "LeftHand"),
        ("LeftHandMiddleEnd", "LeftHand"),
        ("RightShoulder", "Chest"),
        ("RightArm", "RightShoulder"),
        ("RightForeArm", "RightArm"),
        ("RightHand", "RightForeArm"),
        ("RightHandThumbEnd", "RightHand"),
        ("RightHandMiddleEnd", "RightHand"),
        ("LeftLeg", "Hips"),
        ("LeftShin", "LeftLeg"),
        ("LeftFoot", "LeftShin"),
        ("LeftToeBase", "LeftFoot"),
        ("RightLeg", "Hips"),
        ("RightShin", "RightLeg"),
        ("RightFoot", "RightShin"),
        ("RightToeBase", "RightFoot"),
    ],
    "somaskel77": [
        ("Hips", None),
        ("Spine1", "Hips"),
        ("Spine2", "Spine1"),
        ("Chest", "Spine2"),
        ("Neck1", "Chest"),
        ("Neck2", "Neck1"),
        ("Head", "Neck2"),
        ("HeadEnd", "Head"),
        ("Jaw", "Head"),
        ("LeftEye", "Head"),
        ("RightEye", "Head"),
        ("LeftShoulder", "Chest"),
        ("LeftArm", "LeftShoulder"),
        ("LeftForeArm", "LeftArm"),
        ("LeftHand", "LeftForeArm"),
        ("LeftHandThumb1", "LeftHand"),
        ("LeftHandThumb2", "LeftHandThumb1"),
        ("LeftHandThumb3", "LeftHandThumb2"),
        ("LeftHandThumbEnd", "LeftHandThumb3"),
        ("LeftHandIndex1", "LeftHand"),
        ("LeftHandIndex2", "LeftHandIndex1"),
        ("LeftHandIndex3", "LeftHandIndex2"),
        ("LeftHandIndex4", "LeftHandIndex3"),
        ("LeftHandIndexEnd", "LeftHandIndex4"),
        ("LeftHandMiddle1", "LeftHand"),
        ("LeftHandMiddle2", "LeftHandMiddle1"),
        ("LeftHandMiddle3", "LeftHandMiddle2"),
        ("LeftHandMiddle4", "LeftHandMiddle3"),
        ("LeftHandMiddleEnd", "LeftHandMiddle4"),
        ("LeftHandRing1", "LeftHand"),
        ("LeftHandRing2", "LeftHandRing1"),
        ("LeftHandRing3", "LeftHandRing2"),
        ("LeftHandRing4", "LeftHandRing3"),
        ("LeftHandRingEnd", "LeftHandRing4"),
        ("LeftHandPinky1", "LeftHand"),
        ("LeftHandPinky2", "LeftHandPinky1"),
        ("LeftHandPinky3", "LeftHandPinky2"),
        ("LeftHandPinky4", "LeftHandPinky3"),
        ("LeftHandPinkyEnd", "LeftHandPinky4"),
        ("RightShoulder", "Chest"),
        ("RightArm", "RightShoulder"),
        ("RightForeArm", "RightArm"),
        ("RightHand", "RightForeArm"),
        ("RightHandThumb1", "RightHand"),
        ("RightHandThumb2", "RightHandThumb1"),
        ("RightHandThumb3", "RightHandThumb2"),
        ("RightHandThumbEnd", "RightHandThumb3"),
        ("RightHandIndex1", "RightHand"),
        ("RightHandIndex2", "RightHandIndex1"),
        ("RightHandIndex3", "RightHandIndex2"),
        ("RightHandIndex4", "RightHandIndex3"),
        ("RightHandIndexEnd", "RightHandIndex4"),
        ("RightHandMiddle1", "RightHand"),
        ("RightHandMiddle2", "RightHandMiddle1"),
        ("RightHandMiddle3", "RightHandMiddle2"),
        ("RightHandMiddle4", "RightHandMiddle3"),
        ("RightHandMiddleEnd", "RightHandMiddle4"),
        ("RightHandRing1", "RightHand"),
        ("RightHandRing2", "RightHandRing1"),
        ("RightHandRing3", "RightHandRing2"),
        ("RightHandRing4", "RightHandRing3"),
        ("RightHandRingEnd", "RightHandRing4"),
        ("RightHandPinky1", "RightHand"),
        ("RightHandPinky2", "RightHandPinky1"),
        ("RightHandPinky3", "RightHandPinky2"),
        ("RightHandPinky4", "RightHandPinky3"),
        ("RightHandPinkyEnd", "RightHandPinky4"),
        ("LeftLeg", "Hips"),
        ("LeftShin", "LeftLeg"),
        ("LeftFoot", "LeftShin"),
        ("LeftToeBase", "LeftFoot"),
        ("LeftToeEnd", "LeftToeBase"),
        ("RightLeg", "Hips"),
        ("RightShin", "RightLeg"),
        ("RightFoot", "RightShin"),
        ("RightToeBase", "RightFoot"),
        ("RightToeEnd", "RightToeBase"),
    ],
    "g1skel34": [
        ("pelvis_skel", None),
        ("left_hip_pitch_skel", "pelvis_skel"),
        ("left_hip_roll_skel", "left_hip_pitch_skel"),
        ("left_hip_yaw_skel", "left_hip_roll_skel"),
        ("left_knee_skel", "left_hip_yaw_skel"),
        ("left_ankle_pitch_skel", "left_knee_skel"),
        ("left_ankle_roll_skel", "left_ankle_pitch_skel"),
        ("left_toe_base", "left_ankle_roll_skel"),
        ("right_hip_pitch_skel", "pelvis_skel"),
        ("right_hip_roll_skel", "right_hip_pitch_skel"),
        ("right_hip_yaw_skel", "right_hip_roll_skel"),
        ("right_knee_skel", "right_hip_yaw_skel"),
        ("right_ankle_pitch_skel", "right_knee_skel"),
        ("right_ankle_roll_skel", "right_ankle_pitch_skel"),
        ("right_toe_base", "right_ankle_roll_skel"),
        ("waist_yaw_skel", "pelvis_skel"),
        ("waist_roll_skel", "waist_yaw_skel"),
        ("waist_pitch_skel", "waist_roll_skel"),
        ("left_shoulder_pitch_skel", "waist_pitch_skel"),
        ("left_shoulder_roll_skel", "left_shoulder_pitch_skel"),
        ("left_shoulder_yaw_skel", "left_shoulder_roll_skel"),
        ("left_elbow_skel", "left_shoulder_yaw_skel"),
        ("left_wrist_roll_skel", "left_elbow_skel"),
        ("left_wrist_pitch_skel", "left_wrist_roll_skel"),
        ("left_wrist_yaw_skel", "left_wrist_pitch_skel"),
        ("left_hand_roll_skel", "left_wrist_yaw_skel"),
        ("right_shoulder_pitch_skel", "waist_pitch_skel"),
        ("right_shoulder_roll_skel", "right_shoulder_pitch_skel"),
        ("right_shoulder_yaw_skel", "right_shoulder_roll_skel"),
        ("right_elbow_skel", "right_shoulder_yaw_skel"),
        ("right_wrist_roll_skel", "right_elbow_skel"),
        ("right_wrist_pitch_skel", "right_wrist_roll_skel"),
        ("right_wrist_yaw_skel", "right_wrist_pitch_skel"),
        ("right_hand_roll_skel", "right_wrist_yaw_skel"),
    ],
    "smplx22": [
        ("pelvis", None),
        ("left_hip", "pelvis"),
        ("right_hip", "pelvis"),
        ("spine1", "pelvis"),
        ("left_knee", "left_hip"),
        ("right_knee", "right_hip"),
        ("spine2", "spine1"),
        ("left_ankle", "left_knee"),
        ("right_ankle", "right_knee"),
        ("spine3", "spine2"),
        ("left_foot", "left_ankle"),
        ("right_foot", "right_ankle"),
        ("neck", "spine3"),
        ("left_collar", "spine3"),
        ("right_collar", "spine3"),
        ("head", "neck"),
        ("left_shoulder", "left_collar"),
        ("right_shoulder", "right_collar"),
        ("left_elbow", "left_shoulder"),
        ("right_elbow", "right_shoulder"),
        ("left_wrist", "left_elbow"),
        ("right_wrist", "right_elbow"),
    ],
}


def load_joints(path: Path) -> list[list[float]]:
    obj = torch.load(path, map_location="cpu")
    if hasattr(obj, "detach"):
        obj = obj.detach().cpu().numpy()
    elif hasattr(obj, "numpy"):
        obj = obj.numpy()
    return [[float(row[0]), float(row[1]), float(row[2])] for row in obj]


def export_one(skel_name: str, assets_root: Path, out_dir: Path) -> bool:
    layout = SKELETON_LAYOUTS.get(skel_name)
    if layout is None:
        print(f"[WARN] No joint layout for {skel_name}; skipped")
        return False

    joints_p = assets_root / skel_name / "joints.p"
    if not joints_p.exists():
        print(f"[WARN] Missing joints.p for {skel_name}; skipped")
        return False

    points = load_joints(joints_p)
    if len(points) != len(layout):
        print(f"[WARN] Joint count mismatch for {skel_name}: points={len(points)} layout={len(layout)}; skipped")
        return False

    name_to_index = {name: idx for idx, (name, _) in enumerate(layout)}
    joints = []

    for i, ((name, parent_name), pos) in enumerate(zip(layout, points)):
        parent_index = -1 if parent_name is None else name_to_index[parent_name]
        joints.append(
            {
                "index": i,
                "name": name,
                "parent_index": parent_index,
                "parent_name": "" if parent_name is None else parent_name,
                "position": [round(pos[0], 6), round(pos[1], 6), round(pos[2], 6)],
            }
        )

    payload = {
        "schema_version": 1,
        "source": "kimodo/assets/skeletons",
        "skeleton_name": skel_name,
        "joint_count": len(joints),
        "units": "meter",
        "space": "world",
        "root_index": 0,
        "joints": joints,
    }

    out_path = out_dir / f"{skel_name}_neutral.json"
    out_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[OK] {skel_name} -> {out_path}")
    return True


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets-root", required=True)
    parser.add_argument("--out-dir", required=True)
    args = parser.parse_args()

    assets_root = Path(args.assets_root)
    out_dir = Path(args.out_dir)

    if not assets_root.exists():
        print(f"[ERROR] assets root not found: {assets_root}")
        return 1

    out_dir.mkdir(parents=True, exist_ok=True)

    exported = 0
    for skel_dir in sorted(p.name for p in assets_root.iterdir() if p.is_dir()):
        if export_one(skel_dir, assets_root, out_dir):
            exported += 1

    print(f"[DONE] Exported {exported} neutral pose JSON files.")
    return 0 if exported > 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())

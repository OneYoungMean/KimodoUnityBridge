import os
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import Mock, call, patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import flowhmr_assets


class FlowHmrAssetTests(unittest.TestCase):
    def _complete_local_tree(self, root: Path) -> Path:
        source = root / 'vendor' / 'FlowHMR'
        (source / 'checkpoints' / 'flowhmr_latest').mkdir(parents=True)
        (source / 'ckpts' / 'yolox').mkdir(parents=True)
        (source / 'assets' / 'body_models' / 'smplh' / 'neutral').mkdir(parents=True)
        (source / 'ckpts' / 'vggt-omega').mkdir(parents=True)
        (source / 'run_v2m_demo_generation.py').write_text('', encoding='utf-8')
        (source / 'checkpoints' / 'flowhmr_latest' / 'flowhmr_latest.ckpt').write_bytes(b'ckpt')
        (source / 'checkpoints' / 'flowhmr_latest' / 'config.yml').write_text('model: test', encoding='utf-8')
        (source / 'ckpts' / 'yolox' / 'yolox_l.pth').write_bytes(b'yolox')
        for relative in flowhmr_assets.FLOWHMR_BUNDLED_FILES + flowhmr_assets.FLOWHMR_GATED_FILES:
            path = source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'asset')
        return source

    def test_complete_local_assets_do_not_download(self):
        with tempfile.TemporaryDirectory() as temp:
            source = self._complete_local_tree(Path(temp))
            with patch.object(flowhmr_assets, '_download_snapshot') as download:
                result = flowhmr_assets.ensure_flowhmr_assets(source)
            self.assertEqual(result.name, 'flowhmr_latest.ckpt')
            download.assert_not_called()

    def test_missing_licensed_assets_report_manual_locations(self):
        with tempfile.TemporaryDirectory() as temp:
            source = self._complete_local_tree(Path(temp))
            for relative in flowhmr_assets.FLOWHMR_GATED_FILES:
                (source / relative).unlink()
            with patch.object(flowhmr_assets, '_download_snapshot', side_effect=RuntimeError('mirror unavailable')):
                with self.assertRaisesRegex(RuntimeError, 'licensed/local assets'):
                    flowhmr_assets.ensure_flowhmr_assets(source)

    def test_source_missing_is_actionable(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaisesRegex(RuntimeError, 'FlowHMR source is missing'):
                flowhmr_assets.ensure_flowhmr_assets(Path(temp))

    def test_modelscope_success_skips_huggingface(self):
        modelscope_download = Mock()
        hf_download = Mock()
        modules = {
            'modelscope': types.SimpleNamespace(snapshot_download=modelscope_download),
            'huggingface_hub': types.SimpleNamespace(snapshot_download=hf_download),
        }
        with tempfile.TemporaryDirectory() as temp, patch.dict(sys.modules, modules):
            target = Path(temp) / 'assets'
            flowhmr_assets._download_snapshot('checkpoint', target, 'ms/repo', 'hf/repo')
        modelscope_download.assert_called_once_with(model_id='ms/repo', local_dir=str(target))
        hf_download.assert_not_called()

    def test_modelscope_nested_checkpoint_snapshot_is_flattened(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp) / 'checkpoint'
            patterns = (
                'checkpoints/flowhmr-0.46B/flowhmr_latest/flowhmr_latest.ckpt',
                'checkpoints/flowhmr-0.46B/flowhmr_latest/config.yml',
            )

            def download(model_id, local_dir, allow_file_pattern):
                self.assertEqual(model_id, 'ms/flowhmr')
                self.assertEqual(allow_file_pattern, list(patterns))
                nested = Path(local_dir) / 'flowhmr-0.46B' / 'flowhmr_latest'
                nested.mkdir(parents=True)
                (nested / 'flowhmr_latest.ckpt').write_bytes(b'checkpoint')
                (nested / 'config.yml').write_text('model: test', encoding='utf-8')

            modelscope_module = types.SimpleNamespace(snapshot_download=Mock(side_effect=download))
            hf_download = Mock()
            with patch.dict(sys.modules, {
                'modelscope': modelscope_module,
                'huggingface_hub': types.SimpleNamespace(snapshot_download=hf_download),
            }):
                flowhmr_assets._download_snapshot(
                    'FlowHMR checkpoint', target, 'ms/flowhmr', 'hf/flowhmr',
                    allow_patterns=patterns, expected_files=('flowhmr_latest.ckpt', 'config.yml'),
                )

            self.assertEqual((target / 'flowhmr_latest.ckpt').read_bytes(), b'checkpoint')
            self.assertEqual((target / 'config.yml').read_text(encoding='utf-8'), 'model: test')
            hf_download.assert_not_called()

    def test_modelscope_failure_falls_back_to_huggingface(self):
        modelscope_download = Mock(side_effect=OSError('mirror down'))
        hf_download = Mock()
        modules = {
            'modelscope': types.SimpleNamespace(snapshot_download=modelscope_download),
            'huggingface_hub': types.SimpleNamespace(snapshot_download=hf_download),
        }
        with tempfile.TemporaryDirectory() as temp, patch.dict(sys.modules, modules):
            target = Path(temp) / 'assets'
            flowhmr_assets._download_snapshot('checkpoint', target, 'ms/repo', 'hf/repo')
        self.assertEqual(modelscope_download.call_count, 1)
        hf_download.assert_called_once_with(repo_id='hf/repo', local_dir=str(target))

    def test_both_download_sources_are_reported_on_failure(self):
        modules = {
            'modelscope': types.SimpleNamespace(snapshot_download=Mock(side_effect=OSError('ms down'))),
            'huggingface_hub': types.SimpleNamespace(snapshot_download=Mock(side_effect=OSError('hf down'))),
        }
        with tempfile.TemporaryDirectory() as temp, patch.dict(sys.modules, modules):
            with self.assertRaisesRegex(RuntimeError, 'ms/repo.*hf/repo'):
                flowhmr_assets._download_snapshot('checkpoint', Path(temp), 'ms/repo', 'hf/repo')

    def test_gated_assets_use_only_configured_modelscope(self):
        with tempfile.TemporaryDirectory() as temp:
            source = self._complete_local_tree(Path(temp))
            for relative in flowhmr_assets.FLOWHMR_GATED_FILES:
                (source / relative).unlink()
            calls = []

            def record_download(label, target_dir, modelscope_repo, huggingface_repo, **kwargs):
                calls.append((label, modelscope_repo, huggingface_repo))
                raise RuntimeError('mirror unavailable')

            with patch.dict('os.environ', {
                'KIMODO_FLOWHMR_VGGT_MODELSCOPE_REPOSITORY': 'mirror/vggt',
                'KIMODO_FLOWHMR_SAM3D_MODELSCOPE_REPOSITORY': 'mirror/sam3d',
            }), patch.object(flowhmr_assets, '_download_snapshot', side_effect=record_download):
                with self.assertRaisesRegex(RuntimeError, 'licensed/local assets'):
                    flowhmr_assets.ensure_flowhmr_assets(source)
            self.assertTrue(calls)
            self.assertTrue(all(not hf_repo for _, _, hf_repo in calls))
            self.assertTrue(any(repo == 'mirror/vggt' for _, repo, _ in calls))
            self.assertTrue(any(repo == 'mirror/sam3d' for _, repo, _ in calls))

    def test_sam3d_assets_download_root_and_nested_files_separately(self):
        with tempfile.TemporaryDirectory() as temp:
            source = self._complete_local_tree(Path(temp))
            for relative, *_ in flowhmr_assets.GATED_REPOSITORIES:
                (source / relative).unlink()
            calls = []

            def record_download(label, target_dir, modelscope_repo, huggingface_repo, **kwargs):
                patterns = tuple(kwargs['allow_patterns'])
                calls.append((target_dir.name, patterns, modelscope_repo, huggingface_repo))
                for relative in patterns:
                    target = target_dir / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(b'downloaded asset')

            with patch.object(flowhmr_assets, '_download_snapshot', side_effect=record_download):
                flowhmr_assets.ensure_flowhmr_assets(source)

            self.assertIn(
                ('sam-3d-body-dinov3', ('model.ckpt', 'model_config.yaml'), 'facebook/sam-3d-body-dinov3', ''),
                calls,
            )
            self.assertIn(
                ('sam-3d-body-dinov3', ('assets/mhr_model.pt',), 'facebook/sam-3d-body-dinov3', ''),
                calls,
            )
            self.assertTrue(all((source / relative).is_file() for relative, *_ in flowhmr_assets.GATED_REPOSITORIES))

    def test_smplh_skeleton_asset_is_joint_only_float32(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'vendor' / 'FlowHMR'
            source = root / 'assets' / 'body_models' / 'smplh' / 'neutral'
            source.mkdir(parents=True)
            vertices = 12
            joints = 4
            betas = 3
            np.savez(
                source / 'model.npz',
                J_regressor=np.ones((joints, vertices), dtype=np.float64),
                shapedirs=np.ones((vertices, 3, betas), dtype=np.float64),
                v_template=np.ones((vertices, 3), dtype=np.float64),
                kintree_table=np.array([[0, 0, 1, 2], [0, 0, 1, 2]], dtype=np.int64),
                posedirs=np.zeros((vertices, 3, 9), dtype=np.float64),
                weights=np.zeros((vertices, joints), dtype=np.float64),
                f=np.zeros((1, 3), dtype=np.int64),
            )
            flowhmr_assets._ensure_smplh_skeleton_asset(root)
            target = source / 'model_skeleton.npz'
            with np.load(target) as compact:
                self.assertEqual(set(compact.files), {'J_template', 'J_shapedirs', 'kintree_table', 'compact_format'})
                self.assertEqual(compact['J_template'].dtype, np.float32)
                self.assertEqual(compact['J_shapedirs'].dtype, np.float32)
                self.assertLess(target.stat().st_size, 30 * 1024 * 1024)

    def test_skeleton_only_assets_do_not_require_full_mesh_or_mesh_regressor(self):
        with tempfile.TemporaryDirectory() as temp:
            source = self._complete_local_tree(Path(temp))
            full_model = source / flowhmr_assets.SMPLH_MODEL
            full_model.write_bytes(b'full model')
            compact = source / flowhmr_assets.SMPLH_SKELETON_MODEL
            compact.write_bytes(b'compact model')
            (source / flowhmr_assets.FLOWHMR_BUNDLED_FILES[0]).unlink()
            with patch.object(flowhmr_assets, '_download_snapshot') as download:
                result = flowhmr_assets.ensure_flowhmr_assets(source, skeleton_only=True)
            self.assertEqual(result.name, 'flowhmr_latest.ckpt')
            download.assert_not_called()

    def test_model_files_are_resolved_under_models_flowhmr(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'vendor' / 'FlowHMR'
            models = root / 'models' / 'FlowHMR'
            source.mkdir(parents=True)
            (source / 'run_v2m_demo_generation.py').write_text('', encoding='utf-8')
            (source / 'assets' / 'body_models').mkdir(parents=True)
            (source / flowhmr_assets.FLOWHMR_BUNDLED_FILES[0]).write_bytes(b'bundled')
            for relative in (
                'checkpoints/flowhmr_latest/flowhmr_latest.ckpt',
                'checkpoints/flowhmr_latest/config.yml',
                'ckpts/yolox/yolox_l.pth',
            ) + flowhmr_assets.FLOWHMR_SKELETON_GATED_FILES:
                target = models / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(b'asset')
            with patch.object(flowhmr_assets, '_download_snapshot') as download:
                result = flowhmr_assets.ensure_flowhmr_assets(source, skeleton_only=True, model_root=models)
            self.assertEqual(result, (models / 'checkpoints/flowhmr_latest/flowhmr_latest.ckpt').resolve())
            self.assertTrue((models / flowhmr_assets.FLOWHMR_BUNDLED_FILES[0]).is_file())
            self.assertFalse((source / 'checkpoints').exists())
            download.assert_not_called()


if __name__ == '__main__':
    unittest.main()

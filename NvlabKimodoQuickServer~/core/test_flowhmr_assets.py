from pathlib import Path
import os
import sys
import tempfile
import types
import unittest
from unittest.mock import Mock, call, patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import flowhmr_assets


class FlowHmrAssetTests(unittest.TestCase):
    def _complete_local_tree(self, root: Path) -> Path:
        source = root / 'vendor' / 'FlowHMR'
        (source / 'checkpoints' / 'flowhmr_latest').mkdir(parents=True)
        (source / 'ckpts' / 'yolox').mkdir(parents=True)
        (source / 'assets' / 'body_models' / 'smplh' / 'neutral').mkdir(parents=True)
        (source / 'ckpts' / 'vggt-omega').mkdir(parents=True)
        (source / 'ckpts' / 'sam-3d-body-dinov3' / 'assets').mkdir(parents=True)
        (source / 'dinov3').mkdir(parents=True)
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
            with patch.dict(os.environ, {'KIMODO_FLOWHMR_DINOV3_REPO': str(source / 'dinov3')}), \
                    patch.object(flowhmr_assets, '_download_snapshot') as download:
                result = flowhmr_assets.ensure_flowhmr_assets(source)
            self.assertEqual(result.name, 'flowhmr_latest.ckpt')
            download.assert_not_called()

    def test_missing_licensed_assets_report_manual_locations(self):
        with tempfile.TemporaryDirectory() as temp:
            source = self._complete_local_tree(Path(temp))
            for relative in flowhmr_assets.FLOWHMR_GATED_FILES:
                (source / relative).unlink()
            with patch.dict(os.environ, {'KIMODO_FLOWHMR_DINOV3_REPO': str(source / 'dinov3')}), \
                    patch.object(flowhmr_assets, '_download_snapshot', side_effect=RuntimeError('mirror unavailable')):
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

            def record_download(label, target_dir, modelscope_repo, huggingface_repo):
                calls.append((label, modelscope_repo, huggingface_repo))
                raise RuntimeError('mirror unavailable')

            with patch.dict('os.environ', {
                'KIMODO_FLOWHMR_VGGT_MODELSCOPE_REPOSITORY': 'mirror/vggt',
                'KIMODO_FLOWHMR_SAM3D_MODELSCOPE_REPOSITORY': 'mirror/sam3d',
                'KIMODO_FLOWHMR_DINOV3_REPO': str(source / 'dinov3'),
            }), patch.object(flowhmr_assets, '_download_snapshot', side_effect=record_download):
                with self.assertRaisesRegex(RuntimeError, 'licensed/local assets'):
                    flowhmr_assets.ensure_flowhmr_assets(source)
            self.assertTrue(calls)
            self.assertTrue(all(not hf_repo for _, _, hf_repo in calls))
            self.assertIn(('ckpts/vggt-omega/vggt_omega_1b_512.pt', 'mirror/vggt', ''), calls)
            self.assertTrue(any(repo == 'mirror/sam3d' for _, repo, _ in calls))


if __name__ == '__main__':
    unittest.main()

"""Pure guard tests: importing the deployment script never starts services."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import MagicMock, patch

spec = importlib.util.spec_from_file_location('deployment', Path(__file__).with_name('deploy.py'))
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


class Guards(unittest.TestCase):
    def test_systemd_repeated_environment_files_are_not_lost(self):
        with patch.object(deploy, 'run', return_value='ActiveState=inactive\nEnvironmentFiles=/a (ignore_errors=no)\nEnvironmentFiles=/b (ignore_errors=no)'):
            result = deploy.show('dummy')
        self.assertEqual(result['EnvironmentFiles'], '/a (ignore_errors=no) /b (ignore_errors=no)')

    def test_systemd_without_environment_file(self):
        with patch.object(deploy, 'run', return_value='ActiveState=inactive\nMainPID=0'):
            self.assertEqual(deploy.show('dummy').get('EnvironmentFiles', ''), '')

    def test_running_writer_blocks_backup(self):
        with patch.object(deploy, 'show', return_value={'ActiveState': 'active', 'MainPID': '123'}):
            with self.assertRaisesRegex(RuntimeError, 'stopped service'):
                deploy.stopped(database=True)

    def test_running_database_blocks_cold_backup(self):
        with patch.object(deploy, 'show', return_value={'ActiveState': 'inactive', 'MainPID': '0'}), \
                patch.object(deploy, 'dbinfo', return_value={'State': {'Running': True}}):
            with self.assertRaisesRegex(RuntimeError, 'stopped production MySQL'):
                deploy.stopped(database=True)

    def test_file_drift_blocks_activation(self):
        with patch.object(deploy, 'digest', return_value='changed'):
            with self.assertRaisesRegex(RuntimeError, 'File drift'):
                deploy.verify_hashes({'/not-read': 'expected'})

    def test_missing_heartbeat_blocks_frontend(self):
        with patch.object(deploy, 'mysql', side_effect=['1', '0']), \
                patch.object(deploy, 'port_open', return_value=True):
            self.assertFalse(deploy.core_ready())

    def test_missing_world_listener_blocks_frontend(self):
        with patch.object(deploy, 'mysql', return_value='1'), \
                patch.object(deploy, 'port_open', return_value=False):
            self.assertFalse(deploy.core_ready())

    def test_node_health_is_plain_text(self):
        self.assertTrue(deploy.healthy_body(4322, 'ok\n'))
        self.assertFalse(deploy.healthy_body(4322, 'an unrelated HTML page'))

    def test_api_health_is_json(self):
        self.assertTrue(deploy.healthy_body(4323, '{"status":"ok"}'))
        self.assertFalse(deploy.healthy_body(4323, '{"status":"error"}'))

    def test_hermes_prepares_packet_and_text_log_directories(self):
        self.assertEqual(deploy.HERMES_WRITABLE_DIRS, ('Logs', 'PacketsLog'))
        for name in deploy.HERMES_WRITABLE_DIRS:
            release = MagicMock()
            release.resolve.return_value = release
            path = release.__truediv__.return_value
            path.exists.return_value = False
            path.is_symlink.return_value = False
            with patch.object(deploy.shutil, 'chown', create=True) as chown:
                self.assertIs(deploy.prepare_hermes_runtime_directory(release, name), path)
                path.mkdir.assert_called_once_with()
                chown.assert_called_once_with(path, user='hermesproxy', group='hermesproxy')
                path.chmod.assert_called_once_with(0o750)

    def test_hermes_rejects_arbitrary_runtime_paths(self):
        with self.assertRaisesRegex(RuntimeError, 'Unexpected Hermes runtime directory'):
            deploy.prepare_hermes_runtime_directory(MagicMock(), '../elsewhere')

    def test_hermes_preserves_existing_runtime_paths(self):
        release = MagicMock()
        release.resolve.return_value = release
        path = release.__truediv__.return_value
        path.exists.return_value = True
        with self.assertRaisesRegex(RuntimeError, 'existing runtime path'):
            deploy.prepare_hermes_runtime_directory(release, 'PacketsLog')
        path.mkdir.assert_not_called()
        path.chmod.assert_not_called()


if __name__ == '__main__':
    unittest.main()

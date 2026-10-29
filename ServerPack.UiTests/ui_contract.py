"""WPF routing contracts; actual helper/core behavior runs in ServerPack.UiTests.vbproj."""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[1] / 'Plain Craft Launcher 2/Pages/PageLaunch/ServerPackPanel.vb').read_text(encoding='utf-8-sig')

class RollbackRouting(unittest.TestCase):
    def test_local_rollback_precedes_remote_prepare_and_validation(self):
        body = SOURCE.split('Private Async Sub ExecuteServerAction', 1)[1]
        self.assertIn('If action = ServerAction.Rollback Then', body)
        local = body.split('If action = ServerAction.Rollback Then', 1)[1].split('End If', 1)[0]
        self.assertIn('Await RollbackLocalInstanceAsync', local)
        self.assertIn('Return', local)
        self.assertLess(body.index('If action = ServerAction.Rollback Then'), body.index('ValidateServer(server)'))
        self.assertLess(body.index('If action = ServerAction.Rollback Then'), body.index('PackService.PrepareAsync'))

    def test_rollback_has_no_managed_or_prepared_gate(self):
        self.assertIn('Private Async Function RollbackLocalInstanceAsync', SOURCE)
        local = SOURCE.split('Private Async Function RollbackLocalInstanceAsync', 1)[1].split('End Function', 1)[0]
        for forbidden in ('RequireManaged(', 'IsManaged(', 'PrepareAsync(', 'ValidateServer('):
            self.assertNotIn(forbidden, local)
        for required in ('FindLocalRollbackRoots(', 'PackService.RollbackLast(', 'RunLockedServerMutationAsync(', 'EnsureSameMinecraftFolder('):
            self.assertIn(required, local)

    def test_multiple_instances_require_explicit_selection(self):
        self.assertIn('Private Async Function RollbackLocalInstanceAsync', SOURCE)
        local = SOURCE.split('Private Async Function RollbackLocalInstanceAsync', 1)[1].split('End Function', 1)[0]
        self.assertIn('candidates.Count > 1', local)
        self.assertIn('MyMsgBoxSelect(', local)
        self.assertIn('HasValue', local)
        self.assertIn('确认回滚', local)

if __name__ == '__main__':
    unittest.main()

"""Execute recognition-based clicks at a deterministic center point."""

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.pipeline import JActionType, JClick


@AgentServer.custom_action("DailyShopClickCenter")
@AgentServer.custom_action("ClickRecognitionCenter")
class ClickRecognitionCenter(CustomAction):
    def run(self, context, argv):
        try:
            x, y, width, height = (int(value) for value in argv.box)
            if width <= 0 or height <= 0:
                return False
            # A two-coordinate target is an exact point, not a random rectangle.
            result = context.run_action_direct(
                JActionType.Click,
                JClick(target=(x + width // 2, y + height // 2)),
            )
            return result is not None and result.success
        except Exception as error:
            print(f"[ClickRecognitionCenter] {argv.node_name}: {error}", flush=True)
            return False

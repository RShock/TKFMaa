"""Recognize supported interruptions within a shared, per-task recovery budget."""

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_recognition import CustomRecognition

from common import safe_json_loads


POPUP_NODES = (
    "TkfmRecoveryAdvertisement",
    "TkfmRecoveryAnnouncement",
    "TkfmRecoveryEventSignIn",
    "TkfmRecoveryBackButton",
    "GameLoginBackFallback",
    "GameLoginExistingGiftFallback",
)


@AgentServer.custom_recognition("TkfmRecoveryPopup")
class TkfmRecoveryPopup(CustomRecognition):
    def analyze(self, context, argv):
        try:
            param = argv.custom_recognition_param
            if isinstance(param, str):
                param = safe_json_loads(param, {})
            target = param.get("target") if isinstance(param, dict) else None
            if target not in (
                "GameCloseGift",
                "GameCloseAnnouncement",
                "GameCloseEventSignIn",
                "TkfmRecoveryBackButtonTarget",
            ):
                return self._miss("invalid_popup_target")
            used = sum(context.get_hit_count(name) for name in POPUP_NODES)
            if used >= 2:
                return self._miss("popup_recovery_limit_reached", used)
            result = context.run_recognition(target, argv.image)
            if result is None or not result.hit:
                return self._miss("popup_not_found" if result is not None else "capture_or_recognition_failed", used)
            return CustomRecognition.AnalyzeResult(
                box=result.box,
                detail={"popup": target, "attempt": used + 1, "limit": 2},
            )
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "popup_recognition_error", "error": str(error)}
            )

    @staticmethod
    def _miss(reason, used=0):
        return CustomRecognition.AnalyzeResult(box=None, detail={"reason": reason, "used": used, "limit": 2})

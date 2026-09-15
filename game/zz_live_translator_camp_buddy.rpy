# 仅由管理器在 Camp Buddy 专属模式安装。通用模式保留游戏原确认屏幕。
# 游戏原确认屏幕将正文绑定到不含中文字形的 TT2020 字体。
# 在模组文件中重定义同名屏幕，并为正文直接指定已配置的中文字体。
screen confirm(message, yes_action, no_action):
    modal True
    zorder 1002
    style_prefix "confirm"

    add "images/interface/dark_overlay.png"
    add "images/interface/confirm/confirm_box.png" xalign 0.5 yalign 0.5

    text _live_translator_confirm_message(message):
        style "text_confirm"
        font _live_translator_font_path("fonts/TT2020StyleE-Regular.ttf")
        xalign 0.5
        yalign 0.5
        xmaximum 800
        yoffset -65

    hbox:
        xalign 0.5
        yalign 0.5
        yoffset 180
        spacing 150

        imagebutton:
            xmaximum 295
            ymaximum 121
            activate_sound "Audio/Buttons/button_accept.ogg"
            idle "images/interface/confirm/confirm_button_yes_idle.png"
            hover "images/interface/confirm/confirm_button_yes_hover.png"
            action [Function(sendCommandToActiveToy, {"command":"Stop"}), yes_action]

        imagebutton:
            xmaximum 295
            ymaximum 121
            activate_sound "Audio/Buttons/button_back.ogg"
            idle "images/interface/confirm/confirm_button_no_idle.png"
            hover "images/interface/confirm/confirm_button_no_hover.png"
            action no_action

    key "game_menu" action no_action

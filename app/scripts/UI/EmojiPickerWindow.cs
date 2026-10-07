using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Categorized emoji picker popup for the chat bar.
/// Offers searchable emojis across smileys, gestures, symbols, food, nature, and objects.
/// </summary>
public partial class EmojiPickerWindow : PanelContainer
{
    private record EmojiItem(string Emoji, string Category, string[] Keywords);

    private static readonly List<EmojiItem> Emojis = new()
    {
        // Smileys & Emotion
        new("😀", "smileys", new[] { "grinning", "happy", "smile" }),
        new("😃", "smileys", new[] { "smiley", "happy", "joy" }),
        new("😄", "smileys", new[] { "smile", "laugh" }),
        new("😁", "smileys", new[] { "grin" }),
        new("😆", "smileys", new[] { "laughing", "xd" }),
        new("😅", "smileys", new[] { "sweat_smile", "nervous" }),
        new("😂", "smileys", new[] { "joy", "tears", "lol" }),
        new("🤣", "smileys", new[] { "rofl", "lol" }),
        new("😊", "smileys", new[] { "blush", "proud" }),
        new("😇", "smileys", new[] { "innocent", "angel" }),
        new("🙂", "smileys", new[] { "slightly_smiling" }),
        new("😉", "smileys", new[] { "wink" }),
        new("😌", "smileys", new[] { "relieved" }),
        new("😍", "smileys", new[] { "heart_eyes", "love" }),
        new("🥰", "smileys", new[] { "smiling_face_with_3_hearts", "adore" }),
        new("😘", "smileys", new[] { "kissing_heart", "kiss" }),
        new("😋", "smileys", new[] { "yum", "delicious" }),
        new("😛", "smileys", new[] { "stuck_out_tongue" }),
        new("😜", "smileys", new[] { "stuck_out_tongue_winking_eye" }),
        new("🤪", "smileys", new[] { "zany_face", "crazy" }),
        new("😎", "smileys", new[] { "sunglasses", "cool" }),
        new("😏", "smileys", new[] { "smirk" }),
        new("😒", "smileys", new[] { "unamused" }),
        new("😞", "smileys", new[] { "disappointed", "sad" }),
        new("😔", "smileys", new[] { "pensive" }),
        new("😟", "smileys", new[] { "worried" }),
        new("😕", "smileys", new[] { "confused" }),
        new("🙁", "smileys", new[] { "slightly_frowning_face" }),
        new("😣", "smileys", new[] { "persevere" }),
        new("😖", "smileys", new[] { "confounded" }),
        new("😫", "smileys", new[] { "tired_face" }),
        new("😩", "smileys", new[] { "weary" }),
        new("🥺", "smileys", new[] { "pleading_face", "puppy" }),
        new("😢", "smileys", new[] { "cry", "tear" }),
        new("😭", "smileys", new[] { "sob", "crying" }),
        new("😤", "smileys", new[] { "triumph" }),
        new("😠", "smileys", new[] { "angry", "mad" }),
        new("😡", "smileys", new[] { "rage" }),
        new("🤬", "smileys", new[] { "cursing_face" }),
        new("🤯", "smileys", new[] { "exploding_head", "mindblown" }),
        new("😳", "smileys", new[] { "flushed" }),
        new("🥵", "smileys", new[] { "hot_face" }),
        new("🥶", "smileys", new[] { "cold_face" }),
        new("😱", "smileys", new[] { "scream", "shock" }),
        new("😨", "smileys", new[] { "fearful" }),
        new("😰", "smileys", new[] { "cold_sweat" }),
        new("😥", "smileys", new[] { "sad_relieved" }),
        new("😓", "smileys", new[] { "sweat" }),
        new("🤔", "smileys", new[] { "thinking", "hmm" }),
        new("🤭", "smileys", new[] { "hand_over_mouth", "giggle" }),
        new("🤫", "smileys", new[] { "shushing_face", "quiet" }),
        new("🤥", "smileys", new[] { "lying_face" }),
        new("😶", "smileys", new[] { "no_mouth" }),
        new("😐", "smileys", new[] { "neutral_face" }),
        new("😑", "smileys", new[] { "expressionless" }),
        new("😬", "smileys", new[] { "grimacing" }),
        new("🙄", "smileys", new[] { "roll_eyes" }),
        new("😴", "smileys", new[] { "sleeping", "zzz" }),
        new("😷", "smileys", new[] { "mask" }),
        new("🤒", "smileys", new[] { "thermometer_face", "sick" }),
        new("🤕", "smileys", new[] { "head_bandage", "hurt" }),
        new("🤢", "smileys", new[] { "nauseated_face" }),
        new("🤮", "smileys", new[] { "vomiting_face" }),
        new("🤧", "smileys", new[] { "sneezing_face" }),
        new("🥳", "smileys", new[] { "partying_face", "celebrate" }),
        new("🥴", "smileys", new[] { "woozy_face" }),
        new("😵", "smileys", new[] { "dizzy_face" }),
        new("🤠", "smileys", new[] { "cowboy_hat_face" }),
        new("😈", "smileys", new[] { "smiling_imp", "devil" }),
        new("👿", "smileys", new[] { "imp" }),
        new("💀", "smileys", new[] { "skull", "dead" }),
        new("☠️", "smileys", new[] { "skull_and_crossbones" }),
        new("👻", "smileys", new[] { "ghost", "spooky" }),
        new("👽", "smileys", new[] { "alien" }),
        new("🤖", "smileys", new[] { "robot" }),
        new("💩", "smileys", new[] { "poop" }),

        // Gestures & Body
        new("👋", "gestures", new[] { "wave", "hello", "bye" }),
        new("🤚", "gestures", new[] { "raised_back_of_hand" }),
        new("🖐️", "gestures", new[] { "raised_hand_with_fingers_splayed" }),
        new("✋", "gestures", new[] { "hand", "stop" }),
        new("🖖", "gestures", new[] { "vulcan_salute", "spock" }),
        new("👌", "gestures", new[] { "ok_hand", "perfect" }),
        new("🤌", "gestures", new[] { "pinched_fingers", "italian" }),
        new("🤏", "gestures", new[] { "pinching_hand", "little" }),
        new("✌️", "gestures", new[] { "v", "peace" }),
        new("🤞", "gestures", new[] { "crossed_fingers", "luck" }),
        new("🤟", "gestures", new[] { "love_you_gesture" }),
        new("🤘", "gestures", new[] { "metal", "rock" }),
        new("🤙", "gestures", new[] { "call_me_hand" }),
        new("👈", "gestures", new[] { "point_left" }),
        new("👉", "gestures", new[] { "point_right" }),
        new("👆", "gestures", new[] { "point_up" }),
        new("👇", "gestures", new[] { "point_down" }),
        new("☝️", "gestures", new[] { "point_up_index" }),
        new("👍", "gestures", new[] { "thumbs_up", "yes", "like" }),
        new("👎", "gestures", new[] { "thumbs_down", "no", "dislike" }),
        new("✊", "gestures", new[] { "fist_raised" }),
        new("👊", "gestures", new[] { "punch", "brofist" }),
        new("🤛", "gestures", new[] { "left_facing_fist" }),
        new("🤜", "gestures", new[] { "right_facing_fist" }),
        new("👏", "gestures", new[] { "clap", "applause" }),
        new("🙌", "gestures", new[] { "raised_hands", "hooray" }),
        new("👐", "gestures", new[] { "open_hands" }),
        new("🤲", "gestures", new[] { "palms_up_together" }),
        new("🤝", "gestures", new[] { "handshake", "deal" }),
        new("🙏", "gestures", new[] { "pray", "thanks", "please" }),
        new("💪", "gestures", new[] { "muscle", "strong" }),
        new("👀", "gestures", new[] { "eyes", "look" }),
        new("👁️", "gestures", new[] { "eye" }),
        new("🧠", "gestures", new[] { "brain" }),
        new("👄", "gestures", new[] { "lips", "kiss" }),

        // Symbols & Hearts
        new("❤️", "symbols", new[] { "heart", "love", "red_heart" }),
        new("🧡", "symbols", new[] { "orange_heart" }),
        new("💛", "symbols", new[] { "yellow_heart" }),
        new("💚", "symbols", new[] { "green_heart" }),
        new("💙", "symbols", new[] { "blue_heart" }),
        new("💜", "symbols", new[] { "purple_heart" }),
        new("🖤", "symbols", new[] { "black_heart" }),
        new("🤍", "symbols", new[] { "white_heart" }),
        new("🤎", "symbols", new[] { "brown_heart" }),
        new("💔", "symbols", new[] { "broken_heart" }),
        new("❣️", "symbols", new[] { "heart_exclamation" }),
        new("💕", "symbols", new[] { "two_hearts" }),
        new("💞", "symbols", new[] { "revolving_hearts" }),
        new("💓", "symbols", new[] { "heartbeat" }),
        new("💗", "symbols", new[] { "heartpulse" }),
        new("💖", "symbols", new[] { "sparkling_heart" }),
        new("💘", "symbols", new[] { "cupid" }),
        new("💝", "symbols", new[] { "gift_heart" }),
        new("✨", "symbols", new[] { "sparkles", "shine", "magic" }),
        new("⭐", "symbols", new[] { "star" }),
        new("🌟", "symbols", new[] { "star2", "glow" }),
        new("💫", "symbols", new[] { "dizzy" }),
        new("💥", "symbols", new[] { "collision", "boom" }),
        new("🔥", "symbols", new[] { "fire", "flame", "lit" }),
        new("💯", "symbols", new[] { "100", "score" }),
        new("💢", "symbols", new[] { "anger" }),
        new("💨", "symbols", new[] { "dash", "wind" }),
        new("💦", "symbols", new[] { "sweat_drops" }),
        new("💤", "symbols", new[] { "zzz" }),
        new("✔️", "symbols", new[] { "check", "yes" }),
        new("❌", "symbols", new[] { "x", "no" }),
        new("⚠️", "symbols", new[] { "warning" }),
        new("⛔", "symbols", new[] { "no_entry" }),
        new("🚫", "symbols", new[] { "prohibited" }),
        new("❓", "symbols", new[] { "question" }),
        new("❗", "symbols", new[] { "exclamation" }),
        new("🎉", "symbols", new[] { "party", "tada" }),
        new("🎊", "symbols", new[] { "confetti_ball" }),
        new("🚀", "symbols", new[] { "rocket" }),
        new("💡", "symbols", new[] { "bulb", "idea" }),
        new("🔔", "symbols", new[] { "bell" }),
        new("💎", "symbols", new[] { "gem", "diamond" }),

        // Food & Drinks
        new("☕", "food", new[] { "coffee", "tea" }),
        new("🍵", "food", new[] { "tea", "matcha" }),
        new("🍺", "food", new[] { "beer" }),
        new("🍻", "food", new[] { "beers", "cheers" }),
        new("🍷", "food", new[] { "wine" }),
        new("🍸", "food", new[] { "cocktail" }),
        new("🍹", "food", new[] { "tropical_drink" }),
        new("🍾", "food", new[] { "champagne" }),
        new("🍰", "food", new[] { "cake", "sweet" }),
        new("🎂", "food", new[] { "birthday", "cake" }),
        new("🧁", "food", new[] { "cupcake" }),
        new("🍕", "food", new[] { "pizza" }),
        new("🍔", "food", new[] { "burger", "hamburger" }),
        new("🍟", "food", new[] { "fries" }),
        new("🌭", "food", new[] { "hotdog" }),
        new("🍿", "food", new[] { "popcorn" }),
        new("🍦", "food", new[] { "icecream" }),
        new("🍩", "food", new[] { "donut" }),
        new("🍪", "food", new[] { "cookie" }),
        new("🍫", "food", new[] { "chocolate_bar" }),
        new("🍎", "food", new[] { "apple" }),
        new("🍓", "food", new[] { "strawberry" }),
        new("🍇", "food", new[] { "grapes" }),
        new("🍉", "food", new[] { "watermelon" }),

        // Animals & Nature
        new("🐶", "nature", new[] { "dog" }),
        new("🐱", "nature", new[] { "cat" }),
        new("🐭", "nature", new[] { "mouse" }),
        new("🐰", "nature", new[] { "rabbit", "bunny" }),
        new("🦊", "nature", new[] { "fox" }),
        new("🐻", "nature", new[] { "bear" }),
        new("🐼", "nature", new[] { "panda" }),
        new("🐨", "nature", new[] { "koala" }),
        new("🐯", "nature", new[] { "tiger" }),
        new("🦁", "nature", new[] { "lion" }),
        new("🐮", "nature", new[] { "cow" }),
        new("🐷", "nature", new[] { "pig" }),
        new("🐸", "nature", new[] { "frog" }),
        new("🐵", "nature", new[] { "monkey" }),
        new("🦄", "nature", new[] { "unicorn" }),
        new("🦋", "nature", new[] { "butterfly" }),
        new("🌸", "nature", new[] { "cherry_blossom", "flower" }),
        new("🌹", "nature", new[] { "rose" }),
        new("🌺", "nature", new[] { "hibiscus" }),
        new("🌻", "nature", new[] { "sunflower" }),
        new("🍀", "nature", new[] { "four_leaf_clover", "clover", "luck" }),
        new("🌴", "nature", new[] { "palm_tree" }),
        new("🌲", "nature", new[] { "evergreen_tree", "tree" }),
        new("☀️", "nature", new[] { "sunny", "sun" }),
        new("🌙", "nature", new[] { "crescent_moon", "moon" }),
        new("🌈", "nature", new[] { "rainbow" }),
        new("⚡", "nature", new[] { "zap", "lightning" }),

        // Objects & Activities
        new("🎮", "objects", new[] { "game", "controller" }),
        new("📱", "objects", new[] { "iphone", "phone" }),
        new("💻", "objects", new[] { "computer", "laptop" }),
        new("📷", "objects", new[] { "camera" }),
        new("🎵", "objects", new[] { "music", "note" }),
        new("🎶", "objects", new[] { "notes" }),
        new("🎧", "objects", new[] { "headphones" }),
        new("🎁", "objects", new[] { "gift", "present" }),
        new("🎈", "objects", new[] { "balloon" }),
        new("🛒", "objects", new[] { "shopping_cart" }),
        new("🔑", "objects", new[] { "key" }),
        new("📦", "objects", new[] { "package", "box" }),
        new("🏠", "objects", new[] { "house", "home" }),
        new("🚗", "objects", new[] { "car" }),
        new("🚲", "objects", new[] { "bike", "bicycle" }),
        new("✈️", "objects", new[] { "airplane", "flight" }),
        new("🚢", "objects", new[] { "ship", "boat" }),
    };

    private LineEdit _searchEdit = null!;
    private GridContainer _grid = null!;
    private string _currentCategory = "smileys";
    private readonly List<Button> _categoryButtons = new();

    public Action<string>? OnEmojiSelected { get; set; }

    public override void _Ready()
    {
        TopLevel = true;
        Visible = false;

        CustomMinimumSize = new Vector2(290, 270);
        Size = new Vector2(290, 270);

        var styleBox = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.12f, 0.14f, 0.96f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = new Color(0.28f, 0.28f, 0.32f, 0.8f),
            ShadowColor = new Color(0, 0, 0, 0.6f),
            ShadowSize = 8,
            ShadowOffset = new Vector2(0, 4)
        };
        AddThemeStyleboxOverride("panel", styleBox);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 6);
        margin.AddThemeConstantOverride("margin_right", 6);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        AddChild(margin);

        var mainBox = new VBoxContainer();
        mainBox.AddThemeConstantOverride("separation", 6);
        margin.AddChild(mainBox);

        // Search bar
        _searchEdit = new LineEdit
        {
            PlaceholderText = L10n.Tr("ui.chat.emoji_search"),
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _searchEdit.AddThemeFontSizeOverride("font_size", 12);
        _searchEdit.TextChanged += OnSearchChanged;
        mainBox.AddChild(_searchEdit);

        // Categories row
        var catRow = new HBoxContainer();
        catRow.AddThemeConstantOverride("separation", 4);
        mainBox.AddChild(catRow);

        AddCategoryButton(catRow, "😀", "smileys", L10n.Tr("ui.chat.emoji_cat_smileys"));
        AddCategoryButton(catRow, "👋", "gestures", L10n.Tr("ui.chat.emoji_cat_gestures"));
        AddCategoryButton(catRow, "❤️", "symbols", L10n.Tr("ui.chat.emoji_cat_symbols"));
        AddCategoryButton(catRow, "☕", "food", L10n.Tr("ui.chat.emoji_cat_food"));
        AddCategoryButton(catRow, "🐱", "nature", L10n.Tr("ui.chat.emoji_cat_nature"));
        AddCategoryButton(catRow, "🎮", "objects", L10n.Tr("ui.chat.emoji_cat_objects"));

        // Scrollable Grid for emojis
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        mainBox.AddChild(scroll);

        _grid = new GridContainer
        {
            Columns = 7,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _grid.AddThemeConstantOverride("h_separation", 4);
        _grid.AddThemeConstantOverride("v_separation", 4);
        scroll.AddChild(_grid);

        PopulateGrid();
    }

    private void AddCategoryButton(HBoxContainer parent, string icon, string categoryKey, string tooltip)
    {
        var btn = new Button
        {
            Text = icon,
            TooltipText = tooltip,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(32, 28),
        };
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.Pressed += () =>
        {
            _currentCategory = categoryKey;
            _searchEdit.Text = "";
            HighlightCategory();
            PopulateGrid();
        };
        _categoryButtons.Add(btn);
        parent.AddChild(btn);
    }

    private void HighlightCategory()
    {
        // No explicit style needed, but could be added if desired
    }

    private void OnSearchChanged(string text)
    {
        PopulateGrid();
    }

    private void PopulateGrid()
    {
        foreach (var child in _grid.GetChildren())
        {
            _grid.RemoveChild(child);
            child.QueueFree();
        }

        string query = _searchEdit.Text.Trim().ToLowerInvariant();
        bool isSearch = !string.IsNullOrEmpty(query);

        foreach (var item in Emojis)
        {
            if (!isSearch)
            {
                if (item.Category != _currentCategory) continue;
            }
            else
            {
                bool matches = false;
                foreach (var kw in item.Keywords)
                {
                    if (kw.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = true;
                        break;
                    }
                }
                if (!matches) continue;
            }

            var btn = new Button
            {
                Text = item.Emoji,
                TooltipText = item.Keywords.Length > 0 ? item.Keywords[0] : "",
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(34, 34),
            };
            btn.AddThemeFontSizeOverride("font_size", 16);
            string capturedEmoji = item.Emoji;
            btn.Pressed += () =>
            {
                OnEmojiSelected?.Invoke(capturedEmoji);
                HidePicker();
            };
            _grid.AddChild(btn);
        }
    }

    public void ShowAt(Vector2 screenPosition)
    {
        Visible = true;
        ResetSize();
        var minSize = GetCombinedMinimumSize();
        float x = Mathf.Clamp(screenPosition.X, 10f, Mathf.Max(10f, GetViewportRect().Size.X - minSize.X - 10f));
        float y = Mathf.Clamp(screenPosition.Y, SLNGWindow.TopInset, Mathf.Max(SLNGWindow.TopInset, GetViewportRect().Size.Y - minSize.Y - 10f));
        Position = new Vector2(x, y);
        _searchEdit.Text = "";
        _searchEdit.GrabFocus();
        PopulateGrid();
    }

    public void HidePicker()
    {
        Visible = false;
    }

    public void Toggle(Vector2 screenPosition)
    {
        if (Visible)
        {
            HidePicker();
        }
        else
        {
            ShowAt(screenPosition);
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            HidePicker();
            AcceptEvent();
            return;
        }
        base._GuiInput(@event);
    }
}

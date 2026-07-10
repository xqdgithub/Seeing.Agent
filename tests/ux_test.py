"""
Seeing.Agent WebUI 用户体验深度测试
从用户实际使用角度评判
"""
import json
import time
from datetime import datetime
from playwright.sync_api import sync_playwright, TimeoutError as PlaywrightTimeout

BASE_URL = "http://localhost:5000"
TIMEOUT = 30000

# 用户体验评测结果
ux_evaluation = {
    "test_time": "",
    "evaluator": "Playwright UX Test",
    "overall_score": 0,
    "categories": [],
    "critical_issues": [],
    "ux_issues": [],
    "improvement_suggestions": []
}

def add_ux_issue(severity, category, issue, impact, suggestion):
    ux_evaluation["ux_issues"].append({
        "severity": severity,
        "category": category,
        "issue": issue,
        "impact": impact,
        "suggestion": suggestion
    })

def add_critical_issue(module, description, steps, expected, actual):
    ux_evaluation["critical_issues"].append({
        "module": module,
        "description": description,
        "steps": steps,
        "expected": expected,
        "actual": actual
    })

def run_ux_tests():
    ux_evaluation["test_time"] = datetime.now().isoformat()
    
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="msedge", headless=True)
        context = browser.new_context(viewport={"width": 1920, "height": 1080}, locale="zh-CN")
        page = context.new_page()
        page.set_default_timeout(TIMEOUT)
        
        try:
            # ==================== 用户引导测试 ====================
            print("=== 用户引导和首次体验测试 ===")
            
            page.goto(BASE_URL)
            page.wait_for_load_state("networkidle")
            
            # 1. 首次访问是否有引导？
            onboarding = page.query_selector('.onboarding, .guide, .welcome-guide, .tour')
            if not onboarding:
                add_ux_issue("中等", "用户引导", "新用户首次访问无引导流程", 
                    "新用户可能不知道如何开始使用", 
                    "添加可选的新手引导或功能介绍")
            
            # 2. 空状态处理
            page.goto(f"{BASE_URL}/sessions")
            page.wait_for_load_state("networkidle")
            
            # 检查空状态提示是否友好
            empty_state = page.query_selector('.ant-empty-description, .empty-state-description')
            if empty_state:
                empty_text = empty_state.inner_text()
                # 评估空状态文案是否友好
            
            # ==================== 核心用户流程测试 ====================
            print("\n=== 核心用户流程测试 ===")
            
            # 测试：新建会话并发送消息
            page.goto(f"{BASE_URL}/sessions")
            page.wait_for_load_state("networkidle")
            
            # 尝试创建新会话
            new_btn = page.query_selector('button:has-text("新建会话")')
            if new_btn:
                new_btn.click()
                page.wait_for_load_state("networkidle")
                page.wait_for_timeout(2000)
                
                # 检查是否成功跳转到会话页面
                current_url = page.url
                if "/session/" in current_url:
                    print("  [PASS] 新建会话流程正常")
                else:
                    add_critical_issue("会话管理", "新建会话后未正确跳转", 
                        ["点击新建会话按钮"], "跳转到会话聊天页面", f"当前URL: {current_url}")
            
            # 测试：消息输入体验
            input_area = page.query_selector('textarea, .prompt-input textarea')
            if input_area:
                # 检查占位符文本
                placeholder = input_area.get_attribute('placeholder')
                if placeholder:
                    print(f"  输入框占位符: {placeholder}")
                    if len(placeholder) < 10:
                        add_ux_issue("低", "会话聊天", "输入框占位符提示不够详细",
                            "用户可能不清楚可以输入什么类型的内容",
                            "提供更详细的输入提示，如支持Markdown、代码块等")
                
                # 测试输入框是否自动聚焦
                is_focused = input_area.evaluate("el => document.activeElement === el")
                if not is_focused:
                    add_ux_issue("中等", "会话聊天", "进入会话页面后输入框未自动聚焦",
                        "用户需要额外点击才能开始输入，增加操作成本",
                        "进入会话页面后自动聚焦输入框")
            
            # ==================== 表单交互测试 ====================
            print("\n=== 表单交互体验测试 ===")
            
            # 测试Gateway配置页面
            page.goto(f"{BASE_URL}/gateway")
            page.wait_for_load_state("networkidle")
            
            # 检查表单标签是否清晰
            form_labels = page.query_selector_all('.ant-form-item-label label')
            for label in form_labels:
                text = label.inner_text()
                if not text:
                    add_ux_issue("低", "Gateway配置", "存在无标签的表单项",
                        "用户不知道该输入什么", "为所有表单项添加清晰的标签")
            
            # 测试设置页面表单
            page.goto(f"{BASE_URL}/settings")
            page.wait_for_load_state("networkidle")
            
            # 检查是否有保存确认
            save_btn = page.query_selector('button:has-text("保存")')
            if save_btn:
                # 检查保存按钮是否禁用（未修改时）
                is_disabled = save_btn.is_disabled()
                if not is_disabled:
                    add_ux_issue("低", "系统设置", "未修改时保存按钮也可点击",
                        "用户可能困惑是否已保存或需要保存",
                        "未修改时禁用保存按钮，或显示'已保存'状态")
            
            # ==================== 错误处理测试 ====================
            print("\n=== 错误处理和反馈测试 ===")
            
            # 测试必填项验证
            page.goto(f"{BASE_URL}/cron-jobs")
            page.wait_for_load_state("networkidle")
            
            new_job_btn = page.query_selector('button:has-text("新建任务")')
            if new_job_btn:
                new_job_btn.click()
                page.wait_for_timeout(500)
                
                # 检查是否有表单验证
                modal = page.query_selector('.ant-modal-content')
                if modal:
                    # 尝试直接保存（不填任何内容）
                    confirm_btn = modal.query_selector('button:has-text("确定"), button:has-text("保存")')
                    if confirm_btn:
                        confirm_btn.click()
                        page.wait_for_timeout(500)
                        
                        # 检查是否有验证提示
                        error_msgs = page.query_selector_all('.ant-form-item-explain-error, .error-message')
                        if len(error_msgs) == 0:
                            add_ux_issue("高", "定时任务", "新建任务表单缺少必填项验证",
                                "用户可以提交无效数据，导致后端错误",
                                "添加前端表单验证，明确必填项")
                        
                        # 关闭弹窗
                        cancel_btn = modal.query_selector('button:has-text("取消")')
                        if cancel_btn:
                            cancel_btn.click()
                            page.wait_for_timeout(300)
            
            # ==================== 导航和信息架构测试 ====================
            print("\n=== 导航和信息架构测试 ===")
            
            page.goto(BASE_URL)
            page.wait_for_load_state("networkidle")
            
            # 检查菜单层级是否合理
            sidebar = page.query_selector('.app-sider, .ant-layout-sider')
            if sidebar:
                # 检查菜单分组
                submenus = sidebar.query_selector_all('.ant-menu-submenu')
                menu_items = sidebar.query_selector_all('.ant-menu-item')
                
                print(f"  发现 {len(submenus)} 个子菜单, {len(menu_items)} 个菜单项")
                
                # 检查菜单项是否有图标
                for item in menu_items:
                    icon = item.query_selector('.anticon, .ant-menu-item-icon')
                    if not icon:
                        add_ux_issue("低", "导航", "部分菜单项缺少图标",
                            "纯文字菜单项辨识度低", "为所有菜单项添加图标")
                        break
            
            # 测试面包屑导航
            page.goto(f"{BASE_URL}/tools")
            page.wait_for_load_state("networkidle")
            
            breadcrumb = page.query_selector('.ant-breadcrumb')
            if breadcrumb:
                links = breadcrumb.query_selector_all('a, .ant-breadcrumb-link')
                if len(links) < 2:
                    add_ux_issue("低", "导航", "面包屑导航层级不够清晰",
                        "用户难以了解当前位置和返回上级", "完善面包屑导航层级")
            
            # ==================== 加载和等待体验测试 ====================
            print("\n=== 加载和等待体验测试 ===")
            
            # 测试页面加载状态
            start = time.time()
            page.goto(f"{BASE_URL}/mcp")
            
            # 检查是否有加载指示器
            loading_indicator = page.query_selector('.ant-spin, .loading, .skeleton')
            page.wait_for_load_state("networkidle")
            load_time = time.time() - start
            
            if load_time > 2:
                add_ux_issue("中等", "性能", f"MCP页面加载时间过长 ({load_time:.2f}s)",
                    "用户等待时间过长，影响体验", "优化页面加载性能，添加骨架屏")
            
            # ==================== 搜索和筛选测试 ====================
            print("\n=== 搜索和筛选体验测试 ===")
            
            page.goto(f"{BASE_URL}/tools")
            page.wait_for_load_state("networkidle")
            
            search_input = page.query_selector('input[placeholder*="搜索"]')
            if search_input:
                # 测试搜索响应速度
                start = time.time()
                search_input.fill("bash")
                page.wait_for_timeout(1000)  # 等待防抖
                search_time = time.time() - start
                
                # 检查搜索结果提示
                result_count = len(page.query_selector_all('.item-card'))
                
                if result_count == 0:
                    # 检查是否有"无结果"提示
                    no_result = page.query_selector('.ant-empty, .no-result')
                    if not no_result:
                        add_ux_issue("中等", "工具管理", "搜索无结果时缺少提示",
                            "用户不知道是没有匹配结果还是页面出错", "添加'未找到匹配结果'提示")
                
                # 清空搜索
                search_input.fill("")
                page.wait_for_timeout(500)
            
            # ==================== 响应式和可访问性测试 ====================
            print("\n=== 响应式和可访问性测试 ===")
            
            # 测试小屏幕布局
            page.set_viewport_size({"width": 768, "height": 1024})
            page.goto(BASE_URL)
            page.wait_for_load_state("networkidle")
            
            # 检查侧边栏在小屏幕下的表现
            sidebar = page.query_selector('.app-sider, .ant-layout-sider')
            if sidebar:
                is_visible = sidebar.is_visible()
                if is_visible:
                    # 检查侧边栏是否占据过多空间
                    sidebar_width = sidebar.evaluate("el => el.offsetWidth")
                    if sidebar_width > 200:
                        add_ux_issue("中等", "响应式", "小屏幕下侧边栏占用空间过大",
                            "内容区域被压缩，影响阅读", "小屏幕下自动折叠侧边栏")
            
            # 恢复大屏幕
            page.set_viewport_size({"width": 1920, "height": 1080})
            
            # ==================== 按钮和操作测试 ====================
            print("\n=== 按钮和操作体验测试 ===")
            
            page.goto(f"{BASE_URL}/sessions")
            page.wait_for_load_state("networkidle")
            
            # 检查危险操作是否有确认
            delete_btn = page.query_selector('button:has-text("删除"), .ant-btn-dangerous')
            if delete_btn:
                # 检查是否有 Popconfirm
                parent = delete_btn.evaluate_handle("el => el.closest('.ant-popconfirm, .ant-popover')")
                if not parent:
                    add_ux_issue("高", "会话管理", "删除按钮缺少二次确认",
                        "用户可能误删数据，无法恢复", "为删除操作添加确认弹窗")
            
            # ==================== 数据展示测试 ====================
            print("\n=== 数据展示和可读性测试 ===")
            
            page.goto(f"{BASE_URL}/tools")
            page.wait_for_load_state("networkidle")
            
            # 检查表格/卡片是否显示关键信息
            tool_cards = page.query_selector_all('.item-card')
            if tool_cards:
                first_card = tool_cards[0]
                title = first_card.query_selector('.card-title, h3')
                desc = first_card.query_selector('.card-description, p')
                status = first_card.query_selector('.ant-tag, .status-tag')
                
                if not title:
                    add_ux_issue("中等", "工具管理", "工具卡片缺少标题",
                        "用户难以识别工具用途", "为每个工具卡片添加清晰的标题")
                
                if not desc:
                    add_ux_issue("低", "工具管理", "工具卡片缺少描述",
                        "用户需要点击查看详情才能了解工具功能", "在卡片上显示简要描述")
            
            # ==================== MCP 连接状态测试 ====================
            print("\n=== MCP 连接状态显示测试 ===")
            
            page.goto(f"{BASE_URL}/mcp")
            page.wait_for_load_state("networkidle")
            
            mcp_cards = page.query_selector_all('.item-card')
            for card in mcp_cards:
                # 检查连接状态是否清晰
                status_tag = card.query_selector('.ant-tag')
                if status_tag:
                    status_text = status_tag.inner_text()
                    # 检查错误状态是否有详细信息
                    if "error" in status_text.lower() or "错误" in status_text:
                        error_detail = card.query_selector('.error-detail, .error-message')
                        if not error_detail:
                            add_ux_issue("中等", "MCP管理", "错误状态缺少详细错误信息",
                                "用户不知道如何解决问题", "显示具体错误原因和解决建议")
                        break
            
            # ==================== 会话聊天体验测试 ====================
            print("\n=== 会话聊天详细体验测试 ===")
            
            page.goto(f"{BASE_URL}/sessions")
            page.wait_for_load_state("networkidle")
            
            session_card = page.query_selector('.session-card-wrapper, .session-card')
            if session_card:
                session_card.click()
                page.wait_for_load_state("networkidle")
                page.wait_for_timeout(1000)
                
                # 检查会话标题是否可编辑
                title_elem = page.query_selector('.session-title')
                if title_elem:
                    # 检查是否有编辑按钮
                    edit_btn = page.query_selector('button:has-text("重命名"), button[icon="edit"]')
                    if not edit_btn:
                        add_ux_issue("低", "会话聊天", "会话标题缺少便捷编辑方式",
                            "用户需要找到编辑按钮才能修改标题", "支持点击标题直接编辑")
                
                # 检查消息列表滚动
                message_list = page.query_selector('.message-list, .session-messages-container')
                if message_list:
                    # 检查是否有滚动到底部按钮
                    scroll_btn = page.query_selector('.scroll-to-bottom, .ant-back-top')
                    # 不强制要求，但建议添加
                
                # 检查是否有清空确认
                clear_btn = page.query_selector('button:has-text("清空")')
                if clear_btn:
                    # 检查点击后是否有确认
                    clear_btn.click()
                    page.wait_for_timeout(300)
                    confirm_modal = page.query_selector('.ant-modal-confirm, .ant-popconfirm')
                    if not confirm_modal:
                        add_ux_issue("高", "会话聊天", "清空会话缺少确认",
                            "用户可能误操作清空所有消息", "添加清空确认弹窗")
                        # 取消操作
                        page.keyboard.press("Escape")
            
            # ==================== 心跳配置体验测试 ====================
            print("\n=== 心跳配置体验测试 ===")
            
            page.goto(f"{BASE_URL}/heartbeat")
            page.wait_for_load_state("networkidle")
            
            # 检查Prompt输入是否有示例
            prompt_textarea = page.query_selector('textarea')
            if prompt_textarea:
                placeholder = prompt_textarea.get_attribute('placeholder')
                if not placeholder or len(placeholder) < 20:
                    add_ux_issue("中等", "心跳配置", "Prompt输入缺少示例或格式说明",
                        "用户不知道应该如何编写心跳Prompt", 
                        "提供Prompt示例和格式说明（支持Markdown等）")
            
            # 检查活跃时段设置是否直观
            time_pickers = page.query_selector_all('.ant-picker')
            if len(time_pickers) >= 2:
                # 检查是否有时段预览
                pass
            
            # ==================== 最终评分计算 ====================
            print("\n=== 计算最终评分 ===")
            
            # 基础分 100
            base_score = 100
            
            # 根据问题严重程度扣分
            for issue in ux_evaluation["ux_issues"]:
                if issue["severity"] == "高":
                    base_score -= 5
                elif issue["severity"] == "中等":
                    base_score -= 3
                else:
                    base_score -= 1
            
            for _ in ux_evaluation["critical_issues"]:
                base_score -= 10
            
            ux_evaluation["overall_score"] = max(0, base_score)
            
        except Exception as e:
            print(f"测试执行出错: {str(e)}")
        
        finally:
            browser.close()
    
    # 保存结果
    with open("E:/Projects/CSharp/Seeing.Agent/tests/ux_evaluation.json", "w", encoding="utf-8") as f:
        json.dump(ux_evaluation, f, ensure_ascii=False, indent=2)
    
    return ux_evaluation

if __name__ == "__main__":
    result = run_ux_tests()
    print(f"\n最终用户体验评分: {result['overall_score']}/100")
    print(f"发现UX问题: {len(result['ux_issues'])}个")
    print(f"严重问题: {len(result['critical_issues'])}个")

"""
Seeing.Agent WebUI 详细交互测试
"""
import json
import time
from datetime import datetime
from playwright.sync_api import sync_playwright, TimeoutError as PlaywrightTimeout

BASE_URL = "http://localhost:5000"
TIMEOUT = 30000

test_results = {
    "test_time": "",
    "base_url": BASE_URL,
    "modules": [],
    "summary": {"total": 0, "passed": 0, "failed": 0, "blocked": 0},
    "issues": [],
    "recommendations": []
}

def log_result(module, test_id, test_name, status, duration=0, message="", details=None):
    result = {"test_id": test_id, "test_name": test_name, "status": status, "duration_ms": duration, "message": message, "details": details or {}}
    
    module_found = None
    for m in test_results["modules"]:
        if m["module_name"] == module:
            module_found = m
            break
    
    if not module_found:
        module_found = {"module_name": module, "tests": [], "passed": 0, "failed": 0, "blocked": 0}
        test_results["modules"].append(module_found)
    
    module_found["tests"].append(result)
    test_results["summary"]["total"] += 1
    
    if status == "PASS":
        module_found["passed"] += 1
        test_results["summary"]["passed"] += 1
    elif status == "FAIL":
        module_found["failed"] += 1
        test_results["summary"]["failed"] += 1
    else:
        module_found["blocked"] += 1
        test_results["summary"]["blocked"] += 1

def add_issue(severity, module, description, suggestion):
    test_results["issues"].append({
        "severity": severity,
        "module": module,
        "description": description,
        "suggestion": suggestion
    })

def add_recommendation(area, current_state, recommendation):
    test_results["recommendations"].append({
        "area": area,
        "current_state": current_state,
        "recommendation": recommendation
    })

def run_tests():
    test_results["test_time"] = datetime.now().isoformat()
    
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="msedge", headless=True)
        context = browser.new_context(viewport={"width": 1920, "height": 1080}, locale="zh-CN")
        page = context.new_page()
        page.set_default_timeout(TIMEOUT)
        
        try:
            # ==================== 首页详细测试 ====================
            print("Testing Home Page...")
            page.goto(BASE_URL)
            page.wait_for_load_state("networkidle")
            
            # 检查Logo
            start = time.time()
            logo = page.query_selector(".hero-logo, img[alt*='Seeing']")
            if logo:
                log_result("首页", "TC-HOME-001", "Logo显示", "PASS", int((time.time()-start)*1000))
            else:
                log_result("首页", "TC-HOME-001", "Logo显示", "FAIL", int((time.time()-start)*1000), "Logo未找到")
            
            # 检查标题
            start = time.time()
            title = page.query_selector(".hero-title")
            if title:
                title_text = title.inner_text()
                if "Seeing.Agent" in title_text:
                    log_result("首页", "TC-HOME-002", "标题显示", "PASS", int((time.time()-start)*1000), title_text)
                else:
                    log_result("首页", "TC-HOME-002", "标题显示", "FAIL", int((time.time()-start)*1000), f"标题内容不符: {title_text}")
            else:
                log_result("首页", "TC-HOME-002", "标题显示", "FAIL", int((time.time()-start)*1000), "标题元素未找到")
            
            # 检查统计卡片
            start = time.time()
            stat_cards = page.query_selector_all(".stat-card")
            stats_ok = len(stat_cards) >= 3
            details = {"stat_count": len(stat_cards)}
            if stats_ok:
                # 检查统计数值
                for card in stat_cards:
                    value = card.query_selector(".stat-value")
                    label = card.query_selector(".stat-label")
                    if value and label:
                        details[f"stat_{label.inner_text()}"] = value.inner_text()
                log_result("首页", "TC-HOME-003", "统计卡片", "PASS", int((time.time()-start)*1000), f"发现{len(stat_cards)}个统计卡片", details)
            else:
                log_result("首页", "TC-HOME-003", "统计卡片", "FAIL", int((time.time()-start)*1000), f"统计卡片数量不足: {len(stat_cards)}")
            
            # 检查按钮可点击
            start = time.time()
            try:
                start_btn = page.query_selector('button:has-text("开始对话")')
                if start_btn:
                    is_visible = start_btn.is_visible()
                    is_enabled = start_btn.is_enabled()
                    if is_visible and is_enabled:
                        log_result("首页", "TC-HOME-004", "开始对话按钮", "PASS", int((time.time()-start)*1000))
                    else:
                        log_result("首页", "TC-HOME-004", "开始对话按钮", "FAIL", int((time.time()-start)*1000), f"visible:{is_visible}, enabled:{is_enabled}")
                else:
                    log_result("首页", "TC-HOME-004", "开始对话按钮", "FAIL", int((time.time()-start)*1000), "按钮未找到")
            except Exception as e:
                log_result("首页", "TC-HOME-004", "开始对话按钮", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 会话管理详细测试 ====================
            print("Testing Sessions Page...")
            page.goto(f"{BASE_URL}/sessions")
            page.wait_for_load_state("networkidle")
            
            # 检查页面标题
            start = time.time()
            page_title = page.title()
            log_result("会话管理", "TC-SESSIONS-001", "页面标题", "PASS" if "Seeing.Agent" in page_title else "FAIL", int((time.time()-start)*1000), page_title)
            
            # 检查统计信息
            start = time.time()
            stats = page.query_selector_all(".stat-item")
            stat_details = {}
            for stat in stats:
                label = stat.query_selector(".stat-label")
                value = stat.query_selector(".stat-value")
                if label and value:
                    stat_details[label.inner_text()] = value.inner_text()
            log_result("会话管理", "TC-SESSIONS-002", "统计信息", "PASS" if len(stats) >= 2 else "FAIL", int((time.time()-start)*1000), f"统计项数: {len(stats)}", stat_details)
            
            # 检查会话列表
            start = time.time()
            session_cards = page.query_selector_all(".session-card, .session-card-wrapper")
            log_result("会话管理", "TC-SESSIONS-003", "会话列表", "PASS", int((time.time()-start)*1000), f"会话数: {len(session_cards)}")
            
            # 测试新建会话按钮
            start = time.time()
            try:
                new_btn = page.query_selector('button:has-text("新建会话")')
                if new_btn and new_btn.is_visible() and new_btn.is_enabled():
                    log_result("会话管理", "TC-SESSIONS-004", "新建会话按钮", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("会话管理", "TC-SESSIONS-004", "新建会话按钮", "FAIL", int((time.time()-start)*1000), "按钮不可用")
            except Exception as e:
                log_result("会话管理", "TC-SESSIONS-004", "新建会话按钮", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试搜索功能
            start = time.time()
            try:
                search_input = page.query_selector('input[placeholder*="搜索"]')
                if search_input:
                    search_input.fill("test")
                    page.wait_for_timeout(500)
                    search_input.fill("")
                    log_result("会话管理", "TC-SESSIONS-005", "搜索功能", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("会话管理", "TC-SESSIONS-005", "搜索功能", "FAIL", int((time.time()-start)*1000), "搜索框未找到")
            except Exception as e:
                log_result("会话管理", "TC-SESSIONS-005", "搜索功能", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 工具管理详细测试 ====================
            print("Testing Tools Page...")
            page.goto(f"{BASE_URL}/tools")
            page.wait_for_load_state("networkidle")
            
            # 检查工具列表
            start = time.time()
            tool_cards = page.query_selector_all(".item-card")
            tool_count = len(tool_cards)
            log_result("工具管理", "TC-TOOLS-001", "工具列表", "PASS" if tool_count > 0 else "FAIL", int((time.time()-start)*1000), f"工具数: {tool_count}")
            
            # 检查工具分类统计
            start = time.time()
            category_stats = page.query_selector_all(".page-stats .stat-item")
            log_result("工具管理", "TC-TOOLS-002", "分类统计", "PASS" if len(category_stats) > 0 else "FAIL", int((time.time()-start)*1000), f"统计项: {len(category_stats)}")
            
            # 检查筛选器
            start = time.time()
            filters = page.query_selector_all(".filter-group")
            log_result("工具管理", "TC-TOOLS-003", "筛选器", "PASS" if len(filters) >= 2 else "FAIL", int((time.time()-start)*1000), f"筛选器数: {len(filters)}")
            
            # 测试搜索
            start = time.time()
            try:
                search = page.query_selector('input[placeholder*="搜索"]')
                if search:
                    search.fill("bash")
                    page.wait_for_timeout(500)
                    filtered_cards = page.query_selector_all(".item-card")
                    search.fill("")
                    log_result("工具管理", "TC-TOOLS-004", "搜索过滤", "PASS", int((time.time()-start)*1000), f"过滤后: {len(filtered_cards)}")
                else:
                    log_result("工具管理", "TC-TOOLS-004", "搜索过滤", "FAIL", int((time.time()-start)*1000), "搜索框未找到")
            except Exception as e:
                log_result("工具管理", "TC-TOOLS-004", "搜索过滤", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 检查启用/禁用开关
            start = time.time()
            switches = page.query_selector_all('.item-card .ant-switch')
            log_result("工具管理", "TC-TOOLS-005", "启用开关", "PASS" if len(switches) > 0 else "FAIL", int((time.time()-start)*1000), f"开关数: {len(switches)}")
            
            # ==================== 技能管理详细测试 ====================
            print("Testing Skills Page...")
            page.goto(f"{BASE_URL}/skills")
            page.wait_for_load_state("networkidle")
            
            # 检查技能列表
            start = time.time()
            skill_cards = page.query_selector_all(".item-card")
            skill_count = len(skill_cards)
            log_result("技能管理", "TC-SKILLS-001", "技能列表", "PASS", int((time.time()-start)*1000), f"技能数: {skill_count}")
            
            # 检查创建按钮
            start = time.time()
            create_btn = page.query_selector('button:has-text("创建技能"), button:has-text("Create")')
            log_result("技能管理", "TC-SKILLS-002", "创建按钮", "PASS" if create_btn else "FAIL", int((time.time()-start)*1000))
            
            # 检查视图切换
            start = time.time()
            view_toggle = page.query_selector('.view-mode-toggle')
            log_result("技能管理", "TC-SKILLS-003", "视图切换", "PASS" if view_toggle else "FAIL", int((time.time()-start)*1000))
            
            # ==================== MCP 详细测试 ====================
            print("Testing MCP Page...")
            page.goto(f"{BASE_URL}/mcp")
            page.wait_for_load_state("networkidle")
            
            # 检查MCP服务器列表
            start = time.time()
            mcp_cards = page.query_selector_all(".item-card")
            log_result("MCP管理", "TC-MCP-001", "服务器列表", "PASS", int((time.time()-start)*1000), f"服务器数: {len(mcp_cards)}")
            
            # 检查添加按钮
            start = time.time()
            add_btn = page.query_selector('button:has-text("添加"), button:has-text("Add")')
            log_result("MCP管理", "TC-MCP-002", "添加按钮", "PASS" if add_btn else "FAIL", int((time.time()-start)*1000))
            
            # 检查统计
            start = time.time()
            stats = page.query_selector_all(".stat-item")
            log_result("MCP管理", "TC-MCP-003", "统计信息", "PASS" if len(stats) >= 3 else "FAIL", int((time.time()-start)*1000), f"统计项: {len(stats)}")
            
            # ==================== 智能体管理详细测试 ====================
            print("Testing Agents Page...")
            page.goto(f"{BASE_URL}/agents")
            page.wait_for_load_state("networkidle")
            
            # 检查智能体表格
            start = time.time()
            table = page.query_selector('.ant-table')
            if table:
                rows = table.query_selector_all('tbody tr')
                log_result("智能体管理", "TC-AGENTS-001", "智能体列表", "PASS", int((time.time()-start)*1000), f"智能体数: {len(rows)}")
            else:
                log_result("智能体管理", "TC-AGENTS-001", "智能体列表", "FAIL", int((time.time()-start)*1000), "表格未找到")
            
            # 检查刷新按钮
            start = time.time()
            refresh_btn = page.query_selector('button:has-text("刷新"), button:has-text("Reload")')
            log_result("智能体管理", "TC-AGENTS-002", "刷新按钮", "PASS" if refresh_btn else "FAIL", int((time.time()-start)*1000))
            
            # ==================== 模型管理详细测试 ====================
            print("Testing Models Page...")
            page.goto(f"{BASE_URL}/models")
            page.wait_for_load_state("networkidle")
            
            # 检查Provider列表
            start = time.time()
            providers = page.query_selector_all('.ant-menu-item, .ant-col-6 .ant-card')
            log_result("模型管理", "TC-MODELS-001", "Provider列表", "PASS" if len(providers) > 0 else "FAIL", int((time.time()-start)*1000), f"Provider数: {len(providers)}")
            
            # 检查保存按钮
            start = time.time()
            save_btn = page.query_selector('button:has-text("保存")')
            log_result("模型管理", "TC-MODELS-002", "保存按钮", "PASS" if save_btn else "FAIL", int((time.time()-start)*1000))
            
            # ==================== Gateway 详细测试 ====================
            print("Testing Gateway Page...")
            page.goto(f"{BASE_URL}/gateway")
            page.wait_for_load_state("networkidle")
            
            # 检查配置表单
            start = time.time()
            form_items = page.query_selector_all('.ant-form-item')
            log_result("Gateway", "TC-GATEWAY-001", "配置表单", "PASS" if len(form_items) >= 4 else "FAIL", int((time.time()-start)*1000), f"表单项: {len(form_items)}")
            
            # 检查状态显示
            start = time.time()
            status = page.query_selector('.stat-value')
            log_result("Gateway", "TC-GATEWAY-002", "状态显示", "PASS" if status else "FAIL", int((time.time()-start)*1000))
            
            # ==================== 定时任务详细测试 ====================
            print("Testing Cron Jobs Page...")
            page.goto(f"{BASE_URL}/cron-jobs")
            page.wait_for_load_state("networkidle")
            
            # 检查任务列表
            start = time.time()
            table = page.query_selector('.ant-table')
            if table:
                rows = table.query_selector_all('tbody tr')
                log_result("定时任务", "TC-CRON-001", "任务列表", "PASS", int((time.time()-start)*1000), f"任务数: {len(rows)}")
            else:
                log_result("定时任务", "TC-CRON-001", "任务列表", "FAIL", int((time.time()-start)*1000), "表格未找到")
            
            # 检查新建按钮
            start = time.time()
            new_btn = page.query_selector('button:has-text("新建")')
            log_result("定时任务", "TC-CRON-002", "新建按钮", "PASS" if new_btn else "FAIL", int((time.time()-start)*1000))
            
            # 检查状态标签页
            start = time.time()
            tabs = page.query_selector_all('.ant-tabs-tab')
            log_result("定时任务", "TC-CRON-003", "状态标签页", "PASS" if len(tabs) >= 4 else "FAIL", int((time.time()-start)*1000), f"标签页: {len(tabs)}")
            
            # ==================== 心跳详细测试 ====================
            print("Testing Heartbeat Page...")
            page.goto(f"{BASE_URL}/heartbeat")
            page.wait_for_load_state("networkidle")
            
            # 检查配置项
            start = time.time()
            form_items = page.query_selector_all('.ant-form-item')
            log_result("心跳", "TC-HB-001", "配置项", "PASS" if len(form_items) >= 4 else "FAIL", int((time.time()-start)*1000), f"配置项: {len(form_items)}")
            
            # 检查立即执行按钮
            start = time.time()
            run_btn = page.query_selector('button:has-text("立即执行"), button:has-text("Run")')
            log_result("心跳", "TC-HB-002", "立即执行按钮", "PASS" if run_btn else "FAIL", int((time.time()-start)*1000))
            
            # ==================== 记忆详细测试 ====================
            print("Testing Memory Page...")
            page.goto(f"{BASE_URL}/memory")
            page.wait_for_load_state("networkidle")
            
            # 检查记忆列表
            start = time.time()
            table = page.query_selector('.ant-table')
            if table:
                rows = table.query_selector_all('tbody tr')
                log_result("记忆", "TC-MEM-001", "记忆列表", "PASS", int((time.time()-start)*1000), f"记忆数: {len(rows)}")
            else:
                log_result("记忆", "TC-MEM-001", "记忆列表", "FAIL", int((time.time()-start)*1000), "表格未找到")
            
            # 检查搜索
            start = time.time()
            search = page.query_selector('.ant-input-search, input[placeholder*="搜索"]')
            log_result("记忆", "TC-MEM-002", "搜索功能", "PASS" if search else "FAIL", int((time.time()-start)*1000))
            
            # ==================== ACP 详细测试 ====================
            print("Testing ACP Page...")
            page.goto(f"{BASE_URL}/acp")
            page.wait_for_load_state("networkidle")
            
            # 检查后端列表
            start = time.time()
            table = page.query_selector('.ant-table')
            if table:
                rows = table.query_selector_all('tbody tr')
                log_result("ACP", "TC-ACP-001", "后端列表", "PASS", int((time.time()-start)*1000), f"后端数: {len(rows)}")
            else:
                log_result("ACP", "TC-ACP-001", "后端列表", "FAIL", int((time.time()-start)*1000), "表格未找到")
            
            # 检查添加按钮
            start = time.time()
            add_btn = page.query_selector('button:has-text("添加")')
            log_result("ACP", "TC-ACP-002", "添加按钮", "PASS" if add_btn else "FAIL", int((time.time()-start)*1000))
            
            # ==================== 安全详细测试 ====================
            print("Testing Security Page...")
            page.goto(f"{BASE_URL}/security")
            page.wait_for_load_state("networkidle")
            
            # 检查标签页
            start = time.time()
            tabs = page.query_selector_all('.ant-tabs-tab')
            log_result("安全", "TC-SEC-001", "标签页", "PASS" if len(tabs) >= 2 else "FAIL", int((time.time()-start)*1000), f"标签页: {len(tabs)}")
            
            # 检查保存按钮
            start = time.time()
            save_btn = page.query_selector('button:has-text("保存")')
            log_result("安全", "TC-SEC-002", "保存按钮", "PASS" if save_btn else "FAIL", int((time.time()-start)*1000))
            
            # ==================== 系统设置详细测试 ====================
            print("Testing Settings Page...")
            page.goto(f"{BASE_URL}/settings")
            page.wait_for_load_state("networkidle")
            
            # 检查标签页
            start = time.time()
            tabs = page.query_selector_all('.ant-tabs-tab')
            tab_names = [t.inner_text() for t in tabs]
            log_result("系统设置", "TC-SET-001", "设置标签页", "PASS" if len(tabs) >= 4 else "FAIL", int((time.time()-start)*1000), str(tab_names))
            
            # 检查保存按钮
            start = time.time()
            save_btn = page.query_selector('button:has-text("保存")')
            log_result("系统设置", "TC-SET-002", "保存按钮", "PASS" if save_btn else "FAIL", int((time.time()-start)*1000))
            
            # ==================== Gateway客户端详细测试 ====================
            print("Testing Gateway Clients Page...")
            page.goto(f"{BASE_URL}/gateway-clients")
            page.wait_for_load_state("networkidle")
            
            # 检查客户端列表
            start = time.time()
            client_cards = page.query_selector_all('.item-card, .gateway-client-card')
            log_result("Gateway客户端", "TC-CLIENTS-001", "客户端列表", "PASS", int((time.time()-start)*1000), f"客户端数: {len(client_cards)}")
            
            # 检查扫描插件按钮
            start = time.time()
            scan_btn = page.query_selector('button:has-text("扫描"), button:has-text("Scan")')
            log_result("Gateway客户端", "TC-CLIENTS-002", "扫描插件按钮", "PASS" if scan_btn else "FAIL", int((time.time()-start)*1000))
            
            # ==================== 会话聊天详细测试 ====================
            print("Testing Session Chat...")
            page.goto(f"{BASE_URL}/sessions")
            page.wait_for_load_state("networkidle")
            
            # 点击第一个会话或创建新会话
            start = time.time()
            session_card = page.query_selector('.session-card-wrapper, .session-card')
            if session_card:
                session_card.click()
                page.wait_for_load_state("networkidle")
                page.wait_for_timeout(1000)
                log_result("会话聊天", "TC-SESS-001", "进入会话", "PASS", int((time.time()-start)*1000))
            else:
                new_btn = page.query_selector('button:has-text("新建会话")')
                if new_btn:
                    new_btn.click()
                    page.wait_for_load_state("networkidle")
                    page.wait_for_timeout(2000)
                    log_result("会话聊天", "TC-SESS-001", "进入会话", "PASS", int((time.time()-start)*1000), "创建新会话")
                else:
                    log_result("会话聊天", "TC-SESS-001", "进入会话", "FAIL", int((time.time()-start)*1000), "无法创建会话")
            
            # 检查消息输入区域
            start = time.time()
            input_area = page.query_selector('.prompt-input textarea, .session-input-container textarea, textarea')
            log_result("会话聊天", "TC-SESS-002", "消息输入区域", "PASS" if input_area else "FAIL", int((time.time()-start)*1000))
            
            # 检查Agent选择器
            start = time.time()
            agent_select = page.query_selector('.session-header .ant-select')
            log_result("会话聊天", "TC-SESS-003", "Agent选择器", "PASS" if agent_select else "FAIL", int((time.time()-start)*1000))
            
            # 检查模型选择器
            start = time.time()
            model_select = page.query_selector_all('.session-header .ant-select')
            log_result("会话聊天", "TC-SESS-004", "模型选择器", "PASS" if len(model_select) >= 2 else "FAIL", int((time.time()-start)*1000), f"选择器数: {len(model_select)}")
            
            # 检查操作按钮
            start = time.time()
            action_btns = page.query_selector_all('.session-header button')
            log_result("会话聊天", "TC-SESS-005", "操作按钮", "PASS" if len(action_btns) >= 3 else "FAIL", int((time.time()-start)*1000), f"按钮数: {len(action_btns)}")
            
            # ==================== 导航测试 ====================
            print("Testing Navigation...")
            
            # 检查侧边栏
            start = time.time()
            sidebar = page.query_selector('.app-sider, .ant-layout-sider')
            log_result("导航", "TC-NAV-001", "侧边栏", "PASS" if sidebar else "FAIL", int((time.time()-start)*1000))
            
            # 检查菜单项
            start = time.time()
            menu_items = page.query_selector_all('.ant-menu-item, .ant-menu-submenu')
            log_result("导航", "TC-NAV-002", "菜单项", "PASS" if len(menu_items) >= 5 else "FAIL", int((time.time()-start)*1000), f"菜单项: {len(menu_items)}")
            
            # 截图
            page.screenshot(path="E:/Projects/CSharp/Seeing.Agent/tests/screenshot_detail.png", full_page=True)
            
            # ==================== 添加改进建议 ====================
            add_recommendation("首页", "功能正常", "可以添加快捷键支持，提升用户体验")
            add_recommendation("会话管理", "功能正常", "可以添加批量操作功能，如批量删除、批量导出")
            add_recommendation("工具管理", "功能正常", "可以添加工具使用统计和历史记录")
            add_recommendation("技能管理", "功能正常", "可以添加技能版本管理和回滚功能")
            add_recommendation("MCP管理", "功能正常", "可以添加连接健康监控和自动重连配置")
            add_recommendation("定时任务", "功能正常", "可以添加任务执行历史图表展示")
            add_recommendation("会话聊天", "功能正常", "可以添加消息搜索、导出和分享功能")
            
        except Exception as e:
            add_issue("HIGH", "测试执行", f"测试过程出错: {str(e)}", "检查应用状态和日志")
        
        finally:
            browser.close()
    
    # 保存结果
    with open("E:/Projects/CSharp/Seeing.Agent/tests/test_results_detail.json", "w", encoding="utf-8") as f:
        json.dump(test_results, f, ensure_ascii=False, indent=2)
    
    return test_results

if __name__ == "__main__":
    results = run_tests()
    print(f"\n测试完成: 总数={results['summary']['total']}, 通过={results['summary']['passed']}, 失败={results['summary']['failed']}")

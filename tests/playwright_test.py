"""
Seeing.Agent WebUI Playwright 测试脚本 (同步版本)
使用 Edge 浏览器测试所有前端功能
"""

import json
import time
from datetime import datetime
from playwright.sync_api import sync_playwright

# 测试配置
BASE_URL = "http://localhost:5000"
TIMEOUT = 30000

# 测试结果存储
test_results = {
    "test_time": datetime.now().isoformat(),
    "base_url": BASE_URL,
    "modules": [],
    "summary": {
        "total": 0,
        "passed": 0,
        "failed": 0,
        "blocked": 0
    }
}

def log_result(module, test_id, test_name, status, duration=0, message=""):
    """记录测试结果"""
    result = {
        "test_id": test_id,
        "test_name": test_name,
        "status": status,
        "duration_ms": duration,
        "message": message,
        "timestamp": datetime.now().isoformat()
    }
    
    module_found = None
    for m in test_results["modules"]:
        if m["module_name"] == module:
            module_found = m
            break
    
    if not module_found:
        module_found = {
            "module_name": module,
            "tests": [],
            "passed": 0,
            "failed": 0,
            "blocked": 0
        }
        test_results["modules"].append(module_found)
    
    module_found["tests"].append(result)
    test_results["summary"]["total"] += 1
    
    if status == "PASS":
        module_found["passed"] += 1
        test_results["summary"]["passed"] += 1
        print(f"  ✓ {test_id}: {test_name} ({duration}ms)")
    elif status == "FAIL":
        module_found["failed"] += 1
        test_results["summary"]["failed"] += 1
        print(f"  ✗ {test_id}: {test_name} - {message} ({duration}ms)")
    else:
        module_found["blocked"] += 1
        test_results["summary"]["blocked"] += 1
        print(f"  ⚠ {test_id}: {test_name} - BLOCKED: {message} ({duration}ms)")


def run_tests():
    """运行所有测试"""
    print("\n" + "="*60)
    print("Seeing.Agent WebUI 自动化测试")
    print("="*60)
    print(f"测试时间: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"基础URL: {BASE_URL}")
    print("="*60 + "\n")
    
    with sync_playwright() as p:
        browser = p.chromium.launch(
            channel="msedge",
            headless=False,
            slow_mo=50
        )
        context = browser.new_context(
            viewport={"width": 1920, "height": 1080},
            locale="zh-CN"
        )
        page = context.new_page()
        page.set_default_timeout(TIMEOUT)
        
        try:
            # ==================== 首页测试 ====================
            print("\n📋 模块1: 首页 (/)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(BASE_URL)
                page.wait_for_load_state("networkidle")
                hero = page.query_selector(".home-hero")
                if hero:
                    log_result("首页", "TC-HOME-001", "页面加载", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("首页", "TC-HOME-001", "页面加载", "FAIL", int((time.time()-start)*1000), "Hero区域未找到")
            except Exception as e:
                log_result("首页", "TC-HOME-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                stats = page.query_selector_all(".stat-card")
                if len(stats) >= 3:
                    log_result("首页", "TC-HOME-002", "统计数据卡片显示", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("首页", "TC-HOME-002", "统计数据卡片显示", "FAIL", int((time.time()-start)*1000), f"统计卡片数量不足: {len(stats)}")
            except Exception as e:
                log_result("首页", "TC-HOME-002", "统计数据卡片显示", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                start_btn = page.query_selector('button:has-text("开始对话")')
                if start_btn:
                    log_result("首页", "TC-HOME-003", "开始对话按钮存在", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("首页", "TC-HOME-003", "开始对话按钮存在", "FAIL", int((time.time()-start)*1000), "按钮未找到")
            except Exception as e:
                log_result("首页", "TC-HOME-003", "开始对话按钮存在", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                new_btn = page.query_selector('button:has-text("新建会话")')
                if new_btn:
                    log_result("首页", "TC-HOME-004", "新建会话按钮存在", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("首页", "TC-HOME-004", "新建会话按钮存在", "FAIL", int((time.time()-start)*1000), "按钮未找到")
            except Exception as e:
                log_result("首页", "TC-HOME-004", "新建会话按钮存在", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 会话管理测试 ====================
            print("\n📋 模块2: 会话管理 (/sessions)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/sessions")
                page.wait_for_load_state("networkidle")
                log_result("会话管理", "TC-SESSIONS-001", "会话列表页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("会话管理", "TC-SESSIONS-001", "会话列表页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                stats = page.query_selector_all(".stat-item")
                if len(stats) >= 2:
                    log_result("会话管理", "TC-SESSIONS-002", "统计数据显示", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("会话管理", "TC-SESSIONS-002", "统计数据显示", "FAIL", int((time.time()-start)*1000), f"统计项数量不足: {len(stats)}")
            except Exception as e:
                log_result("会话管理", "TC-SESSIONS-002", "统计数据显示", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                new_btn = page.query_selector('button:has-text("新建会话")')
                if new_btn:
                    log_result("会话管理", "TC-SESSIONS-003", "新建会话按钮存在", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("会话管理", "TC-SESSIONS-003", "新建会话按钮存在", "FAIL", int((time.time()-start)*1000), "按钮未找到")
            except Exception as e:
                log_result("会话管理", "TC-SESSIONS-003", "新建会话按钮存在", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 工具管理测试 ====================
            print("\n📋 模块3: 工具管理 (/tools)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/tools")
                page.wait_for_load_state("networkidle")
                tool_cards = page.query_selector_all(".item-card")
                if len(tool_cards) > 0:
                    log_result("工具管理", "TC-TOOLS-001", "工具列表加载", "PASS", int((time.time()-start)*1000), f"发现 {len(tool_cards)} 个工具")
                else:
                    log_result("工具管理", "TC-TOOLS-001", "工具列表加载", "FAIL", int((time.time()-start)*1000), "工具列表为空")
            except Exception as e:
                log_result("工具管理", "TC-TOOLS-001", "工具列表加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                search = page.query_selector('input[placeholder*="搜索"]')
                if search:
                    log_result("工具管理", "TC-TOOLS-002", "搜索框存在", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("工具管理", "TC-TOOLS-002", "搜索框存在", "FAIL", int((time.time()-start)*1000), "搜索框未找到")
            except Exception as e:
                log_result("工具管理", "TC-TOOLS-002", "搜索框存在", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 技能管理测试 ====================
            print("\n📋 模块4: 技能管理 (/skills)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/skills")
                page.wait_for_load_state("networkidle")
                log_result("技能管理", "TC-SKILLS-001", "技能页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("技能管理", "TC-SKILLS-001", "技能页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                create_btn = page.query_selector('button:has-text("创建技能"), button:has-text("Create Skill")')
                if create_btn:
                    log_result("技能管理", "TC-SKILLS-002", "创建技能按钮存在", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("技能管理", "TC-SKILLS-002", "创建技能按钮存在", "FAIL", int((time.time()-start)*1000), "按钮未找到")
            except Exception as e:
                log_result("技能管理", "TC-SKILLS-002", "创建技能按钮存在", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== MCP管理测试 ====================
            print("\n📋 模块5: MCP管理 (/mcp)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/mcp")
                page.wait_for_load_state("networkidle")
                log_result("MCP管理", "TC-MCP-001", "MCP页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("MCP管理", "TC-MCP-001", "MCP页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                add_btn = page.query_selector('button:has-text("添加客户端"), button:has-text("Add")')
                if add_btn:
                    log_result("MCP管理", "TC-MCP-002", "添加客户端按钮存在", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("MCP管理", "TC-MCP-002", "添加客户端按钮存在", "FAIL", int((time.time()-start)*1000), "按钮未找到")
            except Exception as e:
                log_result("MCP管理", "TC-MCP-002", "添加客户端按钮存在", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 智能体管理测试 ====================
            print("\n📋 模块6: 智能体管理 (/agents)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/agents")
                page.wait_for_load_state("networkidle")
                log_result("智能体管理", "TC-AGENTS-001", "智能体页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("智能体管理", "TC-AGENTS-001", "智能体页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                table = page.query_selector('.ant-table, table')
                if table:
                    rows = table.query_selector_all('tbody tr')
                    log_result("智能体管理", "TC-AGENTS-002", f"智能体列表显示", "PASS", int((time.time()-start)*1000), f"发现 {len(rows)} 个智能体")
                else:
                    log_result("智能体管理", "TC-AGENTS-002", "智能体列表显示", "FAIL", int((time.time()-start)*1000), "表格未找到")
            except Exception as e:
                log_result("智能体管理", "TC-AGENTS-002", "智能体列表显示", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 模型管理测试 ====================
            print("\n📋 模块7: 模型管理 (/models)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/models")
                page.wait_for_load_state("networkidle")
                log_result("模型管理", "TC-MODELS-001", "模型页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("模型管理", "TC-MODELS-001", "模型页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== Gateway测试 ====================
            print("\n📋 模块8: Gateway配置 (/gateway)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/gateway")
                page.wait_for_load_state("networkidle")
                log_result("Gateway", "TC-GATEWAY-001", "Gateway页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("Gateway", "TC-GATEWAY-001", "Gateway页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 定时任务测试 ====================
            print("\n📋 模块9: 定时任务 (/cron-jobs)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/cron-jobs")
                page.wait_for_load_state("networkidle")
                log_result("定时任务", "TC-CRON-001", "定时任务页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("定时任务", "TC-CRON-001", "定时任务页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 心跳测试 ====================
            print("\n📋 模块10: 心跳配置 (/heartbeat)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/heartbeat")
                page.wait_for_load_state("networkidle")
                log_result("心跳", "TC-HB-001", "心跳页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("心跳", "TC-HB-001", "心跳页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 记忆测试 ====================
            print("\n📋 模块11: 记忆管理 (/memory)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/memory")
                page.wait_for_load_state("networkidle")
                log_result("记忆", "TC-MEM-001", "记忆页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("记忆", "TC-MEM-001", "记忆页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== ACP测试 ====================
            print("\n📋 模块12: ACP配置 (/acp)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/acp")
                page.wait_for_load_state("networkidle")
                log_result("ACP", "TC-ACP-001", "ACP页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("ACP", "TC-ACP-001", "ACP页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 安全测试 ====================
            print("\n📋 模块13: 安全配置 (/security)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/security")
                page.wait_for_load_state("networkidle")
                log_result("安全", "TC-SEC-001", "安全页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("安全", "TC-SEC-001", "安全页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 系统设置测试 ====================
            print("\n📋 模块14: 系统设置 (/settings)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/settings")
                page.wait_for_load_state("networkidle")
                log_result("系统设置", "TC-SET-001", "设置页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("系统设置", "TC-SET-001", "设置页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                tabs = page.query_selector_all('.ant-tabs-tab')
                if len(tabs) >= 4:
                    log_result("系统设置", "TC-SET-002", f"设置标签页显示", "PASS", int((time.time()-start)*1000), f"发现 {len(tabs)} 个标签页")
                else:
                    log_result("系统设置", "TC-SET-002", "设置标签页显示", "FAIL", int((time.time()-start)*1000), f"标签页数量不足: {len(tabs)}")
            except Exception as e:
                log_result("系统设置", "TC-SET-002", "设置标签页显示", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== Gateway客户端测试 ====================
            print("\n📋 模块15: Gateway客户端 (/gateway-clients)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/gateway-clients")
                page.wait_for_load_state("networkidle")
                log_result("Gateway客户端", "TC-CLIENTS-001", "Gateway客户端页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("Gateway客户端", "TC-CLIENTS-001", "Gateway客户端页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 会话聊天测试 ====================
            print("\n📋 模块16: 会话聊天 (/session)")
            print("-"*40)
            
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/sessions")
                page.wait_for_load_state("networkidle")
                session_card = page.query_selector('.session-card-wrapper, .session-card')
                if session_card:
                    session_card.click()
                    page.wait_for_load_state("networkidle")
                    page.wait_for_timeout(1000)
                    log_result("会话聊天", "TC-SESS-001", "会话聊天页面加载", "PASS", int((time.time()-start)*1000))
                else:
                    new_btn = page.query_selector('button:has-text("新建会话")')
                    if new_btn:
                        new_btn.click()
                        page.wait_for_load_state("networkidle")
                        page.wait_for_timeout(2000)
                        log_result("会话聊天", "TC-SESS-001", "会话聊天页面加载", "PASS", int((time.time()-start)*1000), "创建新会话")
                    else:
                        log_result("会话聊天", "TC-SESS-001", "会话聊天页面加载", "FAIL", int((time.time()-start)*1000), "无法创建会话")
            except Exception as e:
                log_result("会话聊天", "TC-SESS-001", "会话聊天页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                input_area = page.query_selector('.session-input-container, .prompt-input, textarea')
                if input_area:
                    log_result("会话聊天", "TC-SESS-002", "消息输入区域存在", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("会话聊天", "TC-SESS-002", "消息输入区域存在", "FAIL", int((time.time()-start)*1000), "输入区域未找到")
            except Exception as e:
                log_result("会话聊天", "TC-SESS-002", "消息输入区域存在", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                agent_select = page.query_selector('.session-header .ant-select')
                if agent_select:
                    log_result("会话聊天", "TC-SESS-003", "Agent选择器存在", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("会话聊天", "TC-SESS-003", "Agent选择器存在", "FAIL", int((time.time()-start)*1000), "选择器未找到")
            except Exception as e:
                log_result("会话聊天", "TC-SESS-003", "Agent选择器存在", "FAIL", int((time.time()-start)*1000), str(e))
            
            # ==================== 导航测试 ====================
            print("\n📋 模块17: 侧边栏导航")
            print("-"*40)
            
            start = time.time()
            try:
                sidebar = page.query_selector('.app-sider, .ant-layout-sider')
                if sidebar:
                    log_result("导航", "TC-NAV-001", "侧边栏显示", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("导航", "TC-NAV-001", "侧边栏显示", "FAIL", int((time.time()-start)*1000), "侧边栏未找到")
            except Exception as e:
                log_result("导航", "TC-NAV-001", "侧边栏显示", "FAIL", int((time.time()-start)*1000), str(e))
            
            start = time.time()
            try:
                menu_items = page.query_selector_all('.ant-menu-item, .ant-menu-submenu')
                if len(menu_items) >= 5:
                    log_result("导航", "TC-NAV-002", f"菜单项显示", "PASS", int((time.time()-start)*1000), f"发现 {len(menu_items)} 个菜单项")
                else:
                    log_result("导航", "TC-NAV-002", "菜单项显示", "FAIL", int((time.time()-start)*1000), f"菜单项数量不足: {len(menu_items)}")
            except Exception as e:
                log_result("导航", "TC-NAV-002", "菜单项显示", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 截图
            page.screenshot(path="E:/Projects/CSharp/Seeing.Agent/tests/test_screenshot_final.png", full_page=True)
            print("\n📸 最终截图已保存")
            
        except Exception as e:
            print(f"\n❌ 测试执行出错: {str(e)}")
        
        finally:
            browser.close()
    
    # 输出测试摘要
    print("\n" + "="*60)
    print("测试摘要")
    print("="*60)
    print(f"总测试数: {test_results['summary']['total']}")
    print(f"通过: {test_results['summary']['passed']} ✓")
    print(f"失败: {test_results['summary']['failed']} ✗")
    print(f"阻塞: {test_results['summary']['blocked']} ⚠")
    if test_results['summary']['total'] > 0:
        print(f"通过率: {test_results['summary']['passed']/test_results['summary']['total']*100:.1f}%")
    print("="*60)
    
    # 保存测试结果
    with open("E:/Projects/CSharp/Seeing.Agent/tests/test_results.json", "w", encoding="utf-8") as f:
        json.dump(test_results, f, ensure_ascii=False, indent=2)
    print("\n📄 测试结果已保存到 test_results.json")
    
    return test_results


if __name__ == "__main__":
    run_tests()